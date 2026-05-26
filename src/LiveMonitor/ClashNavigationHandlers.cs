// LiveMonitor/ClashNavigationHandlers.cs  — v5.0
//
// All ExternalEvent handlers used by ClashRadarPanel buttons.
// ExternalEvent is the ONLY safe way to call Revit API from WPF button handlers.
//
// Handlers:
//   ClashNavHandler    — Show 3D or Show 2D view, zoomed to clash elements
//   ClashRefreshHandler— Re-scan changed elements, prune resolved clashes
//   ClashExportHandler — Export current radar list to CSV

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using ClashEngineNS = ClashResolveAI.ClashEngine.ClashEngine;

namespace ClashResolveAI.LiveMonitor
{
    // ══════════════════════════════════════════════════════════════════
    //  NAVIGATE TO CLASH  (Show 3D / Show 2D)
    // ══════════════════════════════════════════════════════════════════

    public enum NavMode { View3D, View2D }

    public class ClashNavHandler : IExternalEventHandler
    {
        public ClashResult? Target { get; set; }
        public NavMode      Mode   { get; set; } = NavMode.View3D;

        public void Execute(UIApplication app)
        {
            if (Target == null) return;
            var uidoc = app.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (doc == null) return;

            try
            {
                var ids = new List<ElementId>();
                if (Target.ElementA?.IsValidObject == true) ids.Add(Target.ElementA.Id);
                if (Target.ElementB?.IsValidObject == true) ids.Add(Target.ElementB.Id);
                if (!ids.Any()) return;

                if (Mode == NavMode.View3D)
                    ShowIn3D(uidoc!, doc, ids, Target);
                else
                    ShowIn2D(uidoc!, doc, ids, Target.ClashPoint);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClashNavHandler] {ex.Message}");
            }
        }

        // ── 3D Navigation ──────────────────────────────────────────────

        private void ShowIn3D(UIDocument uidoc, Document doc,
                               List<ElementId> ids, ClashResult clash)
        {
            View3D? view = null;
            const string ViewName = "Clash_Radar_3D";

            // Find existing radar view (reuse to avoid clutter)
            view = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName);

            using var tx = new Transaction(doc, "Clash Radar — Navigate 3D");
            tx.Start();

            if (view == null)
            {
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft != null)
                {
                    view = View3D.CreateIsometric(doc, vft.Id);
                    view.Name = ViewName;
                }
            }

            if (view == null) { tx.RollBack(); return; }

            // Remove any prior temporary isolation before applying new one
            if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);

            // Tight section box around the clash point
            XYZ cp    = clash.ClashPoint ?? XYZ.Zero;
            double pad = 3.0; // feet
            view.SetSectionBox(new BoundingBoxXYZ
            {
                Min = new XYZ(cp.X - pad, cp.Y - pad, cp.Z - pad),
                Max = new XYZ(cp.X + pad, cp.Y + pad, cp.Z + pad)
            });
            view.IsSectionBoxActive = true;

            view.IsolateElementsTemporary(ids);
            view.DetailLevel  = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.ShadingWithEdges;

            tx.Commit();

            uidoc.ActiveView = view;
            uidoc.ShowElements(ids);
        }

        // ── 2D Navigation ──────────────────────────────────────────────

        private void ShowIn2D(UIDocument uidoc, Document doc,
                               List<ElementId> ids, XYZ? clashPoint)
        {
            if (clashPoint == null) return;

            // Find the floor plan view whose level is closest in elevation
            var planView = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .OrderBy(v =>
                {
                    double levelZ = v.GenLevel?.Elevation ?? 0;
                    return Math.Abs(levelZ - clashPoint.Z);
                })
                .FirstOrDefault();

            if (planView == null) return;

            uidoc.ActiveView = planView;

            // Select elements and zoom
            uidoc.Selection.SetElementIds(ids);
            
            // Zoom to a small box around clash point in 2D
            double p = 2.0;
            var bb = new BoundingBoxXYZ {
                Min = new XYZ(clashPoint.X - p, clashPoint.Y - p, clashPoint.Z - p),
                Max = new XYZ(clashPoint.X + p, clashPoint.Y + p, clashPoint.Z + p)
            };
            uidoc.ShowElements(ids);
        }

        public string GetName() => "ClashResolveAI_RadarNav";
    }

    // ══════════════════════════════════════════════════════════════════
    //  REFRESH  — Re-test existing clashes, add new ones from selection
    // ══════════════════════════════════════════════════════════════════

    public class ClashRefreshHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc   = uidoc?.Document;
                if (doc == null || doc.IsReadOnly) return;

                var store = RadarDataStore.Instance;

                // 1. Prune resolved clashes using quick AABB check
                store.PruneResolved(clash =>
                {
                    try
                    {
                        var elA = clash.ElementA;
                        var elB = clash.ElementB;
                        if (elA == null || !elA.IsValidObject) return false;
                        if (elB == null || !elB.IsValidObject) return false;
                        var bbA = elA.get_BoundingBox(null);
                        var bbB = elB.get_BoundingBox(null);
                        if (bbA == null || bbB == null) return false;
                        // Still intersecting?
                        return bbA.Min.X <= bbB.Max.X && bbA.Max.X >= bbB.Min.X
                            && bbA.Min.Y <= bbB.Max.Y && bbA.Max.Y >= bbB.Min.Y
                            && bbA.Min.Z <= bbB.Max.Z && bbA.Max.Z >= bbB.Min.Z;
                    }
                    catch { return false; }
                });

                // 2. Scan current selection for new clashes
                var selIds = uidoc!.Selection.GetElementIds();
                if (selIds?.Count > 0)
                {
                    var engine = new ClashEngineNS(doc);
                    foreach (var id in selIds)
                    {
                        try
                        {
                            var el = doc.GetElement(id);
                            if (el == null) continue;
                            var clashes = engine.RunSelectionScan(el);
                            if (clashes.Any())
                                store.AddClashes(clashes);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClashRefreshHandler] {ex.Message}");
            }
        }

        public string GetName() => "ClashResolveAI_RadarRefresh";
    }

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT  — Dump active radar list to CSV (runs on UI thread via dialog)
    //  No Revit API needed — pure file I/O, safe to call directly.
    // ══════════════════════════════════════════════════════════════════

    public static class RadarExporter
    {
        public static void ExportToCsv(List<ClashResult> clashes)
        {
            if (!clashes.Any())
            {
                MessageBox.Show("No active clashes to export.", "Clash Radar");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title    = "Export Clash Radar List",
                Filter   = "CSV files (*.csv)|*.csv",
                FileName = $"ClashRadar_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var sb = new StringBuilder();
            sb.AppendLine("#,Time,Category A,Category B,ID A,ID B,Severity,Gap(mm),Location,GridRef");

            int i = 1;
            foreach (var c in clashes)
            {
                string catA  = c.ElementA?.Category?.Name ?? c.DisciplineA.ToString();
                string catB  = c.ElementB?.Category?.Name ?? c.DisciplineB.ToString();
                string idA   = c.ElementA?.Id.Value.ToString() ?? "";
                string idB   = c.ElementB?.Id.Value.ToString() ?? "";
                string sev   = c.Severity.ToString();
                string loc   = string.IsNullOrEmpty(c.LocationText) ? c.ZoneName : c.LocationText;
                sb.AppendLine($"{i++},{DateTime.Now:HH:mm},{Q(catA)},{Q(catB)},{idA},{idB},{sev},{c.GapMM:F1},{Q(loc)},{Q(c.GridRef)}");
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            System.Diagnostics.Process.Start(dlg.FileName);
        }

        private static string Q(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
