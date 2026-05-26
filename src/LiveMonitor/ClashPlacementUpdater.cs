using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    public class ClashPlacementUpdater : IUpdater
    {
        private readonly AddInId _addInId;
        private readonly UpdaterId _updaterId;

        public ClashPlacementUpdater(AddInId addInId)
        {
            _addInId = addInId;
            // Stable Guid for the updater
            _updaterId = new UpdaterId(_addInId, new Guid("7F8C8D23-2428-428D-9999-510000000001"));
        }

        public void Execute(UpdaterData data)
        {
            var doc = data.GetDocument();
            // Updaters should not run in read-only mode, but good to check
            if (doc.IsReadOnly) return;

            var added    = data.GetAddedElementIds();
            var modified = data.GetModifiedElementIds();
            var allIds   = added.Concat(modified).ToList();

            if (!allIds.Any()) return;

            // Use the same engine as live monitor for speed
            var engine = new ClashResolveAI.ClashEngine.ClashEngine(doc);

            foreach (var id in allIds)
            {
                var el = doc.GetElement(id);
                if (el == null || !ElementCollector.IsMonitoredClashElement(el)) continue;

                // Fast scan including links (transformed AABB)
                var clashes = engine.RunSelectionScanWithLinks(el);
                
                // Block ONLY Hard clashes
                var hardClashes = clashes.Where(c => c.Severity == ClashSeverity.Hard).ToList();
                if (hardClashes.Any())
                {
                    FailureMessage fm = new FailureMessage(ClashFailures.HardClashFailure);
                    var failingIds = new List<ElementId> { el.Id };
                    if (hardClashes[0].ElementB != null) failingIds.Add(hardClashes[0].ElementB.Id);
                    fm.SetFailingElements(failingIds);
                    
                    doc.PostFailure(fm);
                    break; 
                }
            }
        }

        public UpdaterId GetUpdaterId() => _updaterId;
        public ChangePriority GetChangePriority() => ChangePriority.MEPFixtures;
        public string GetUpdaterName() => "ClashResolve AI — Hard Clash Placement Blocker";
        public string GetAdditionalInformation() => "Real-time prevention of hard solid-to-solid interferences.";

        public static void Register(AddInId addInId)
        {
            var updater = new ClashPlacementUpdater(addInId);
            if (!UpdaterRegistry.IsUpdaterRegistered(updater.GetUpdaterId()))
            {
                UpdaterRegistry.RegisterUpdater(updater, true);
                
                // Collect all MEP categories to watch
                var categories = new List<ElementId>();
                var disciplines = new[] { 
                    Discipline.HVAC, Discipline.Plumbing, Discipline.Electrical, 
                    Discipline.FireProtection, Discipline.MedicalGas, 
                    Discipline.GravityDrainage, Discipline.CableTray, Discipline.Conduit 
                };

                foreach (var disc in disciplines)
                {
                    foreach (var bic in ElementCollector.GetCategoriesForDiscipline(disc))
                    {
                        categories.Add(new ElementId(bic));
                    }
                }

                if (categories.Any())
                {
                    ElementMulticategoryFilter filter = new ElementMulticategoryFilter(categories);
                    UpdaterRegistry.AddTrigger(updater.GetUpdaterId(), filter, Element.GetChangeTypeElementAddition());
                    UpdaterRegistry.AddTrigger(updater.GetUpdaterId(), filter, Element.GetChangeTypeGeometry());
                }
            }
        }

        public static void Unregister(AddInId addInId)
        {
            var updaterId = new UpdaterId(addInId, new Guid("7F8C8D23-2428-428D-9999-510000000001"));
            if (UpdaterRegistry.IsUpdaterRegistered(updaterId))
            {
                UpdaterRegistry.UnregisterUpdater(updaterId);
            }
        }
    }

    public static class ClashFailures
    {
        public static FailureDefinitionId HardClashFailure = 
            new FailureDefinitionId(new Guid("7F8C8D23-2428-428D-9999-510000000002"));

        public static void Register()
        {
            try
            {
                FailureDefinition.CreateFailureDefinition(
                    HardClashFailure,
                    FailureSeverity.Error,
                    "ClashResolve AI: Hard clash detected. Placement is blocked to preserve coordination integrity.");
            }
            catch { }
        }
    }
}
