// Core/LinkedModelManager.cs  — v3.3
//
// STEP 2: Linked elements are cached after the first collection.
//         Subsequent calls return the cache — no repeated FilteredElementCollector
//         calls across all linked documents. Cache invalidated by InvalidateCache().
//
// STEP 6: Only necessary MEP categories are scanned — no furniture, walls,
//         generic models, or categories that never clash with MEP.

using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashResolveAI.Links
{
    public class LinkedModel
    {
        public RevitLinkInstance Instance   { get; set; } = null!;
        public Document          LinkDoc    { get; set; } = null!;
        public Transform         Transform  { get; set; } = Transform.Identity;
        public string            FileName   { get; set; } = "";
        public LinkDiscipline    Discipline { get; set; }
        public bool              IsLoaded   { get; set; }
    }

    public enum LinkDiscipline
    {
        Architectural,
        Structural,
        MEP_Mechanical,
        MEP_Electrical,
        MEP_Plumbing,
        MEP_FireProtection,
        Civil,
        Unknown
    }

    public class LinkedElement
    {
        public Element         Element       { get; set; } = null!;
        public Document        SourceDoc     { get; set; } = null!;
        public Transform       LinkTransform { get; set; } = Transform.Identity;
        public string          LinkFileName  { get; set; } = "";
        public LinkDiscipline  LinkDisc      { get; set; }
        public BoundingBoxXYZ? TransformedBB { get; set; }

        public Discipline MepDiscipline
        {
            get
            {
                switch (LinkDisc)
                {
                    case LinkDiscipline.Structural:         return Discipline.Structural;
                    case LinkDiscipline.Architectural:      return Discipline.Structural;
                    case LinkDiscipline.MEP_Mechanical:     return Discipline.HVAC;
                    case LinkDiscipline.MEP_Electrical:     return Discipline.Electrical;
                    case LinkDiscipline.MEP_Plumbing:       return Discipline.Plumbing;
                    case LinkDiscipline.MEP_FireProtection: return Discipline.FireProtection;
                    default:                                return Discipline.Unknown;
                }
            }
        }
    }

    public class LinkedModelManager
    {
        private readonly Document     _hostDoc;
        private List<LinkedModel>?    _linkCache;

        // ── STEP 2: Cached linked elements ───────────────────────────────
        // Populated once on first call to GetAllLinkedElements().
        // Calling InvalidateCache() forces a refresh (e.g. after reload).
        private List<LinkedElement>?  _elementCache;

        public LinkedModelManager(Document hostDoc)
        {
            _hostDoc = hostDoc;
        }

        // Invalidate both caches — call when links are reloaded
        public void InvalidateCache()
        {
            _linkCache    = null;
            _elementCache = null;
            Debug.WriteLine("[ClashResolve] LinkedModelManager cache invalidated.");
        }

        // ── Get all linked model instances ────────────────────────────────

        public List<LinkedModel> GetAllLinks(bool forceRefresh = false)
        {
            if (_linkCache != null && !forceRefresh) return _linkCache;

            _linkCache = new List<LinkedModel>();

            var instances = new FilteredElementCollector(_hostDoc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var inst in instances)
            {
                try
                {
                    var linkDoc  = inst.GetLinkDocument();
                    var fileName = GetLinkFileName(inst);

                    if (linkDoc == null)
                    {
                        _linkCache.Add(new LinkedModel
                        {
                            Instance   = inst,
                            LinkDoc    = null!,
                            Transform  = inst.GetTotalTransform(),
                            FileName   = fileName,
                            Discipline = LinkDiscipline.Unknown,
                            IsLoaded   = false
                        });
                        continue;
                    }

                    var disc = DetectDiscipline(linkDoc, fileName);
                    _linkCache.Add(new LinkedModel
                    {
                        Instance   = inst,
                        LinkDoc    = linkDoc,
                        Transform  = inst.GetTotalTransform(),
                        FileName   = fileName,
                        Discipline = disc,
                        IsLoaded   = true
                    });

                    Debug.WriteLine(string.Concat(
                        "[ClashResolve] Link loaded: [", disc.ToString(), "] ", fileName));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Concat(
                        "[ClashResolve] Failed to load link: ", ex.Message));
                }
            }

            return _linkCache;
        }

        public List<LinkedModel> GetLoadedLinks() =>
            GetAllLinks().Where(l => l.IsLoaded && l.LinkDoc != null).ToList();

        // ── STEP 2: Cached element collection ────────────────────────────
        // First call collects all linked elements (slow).
        // Subsequent calls return the cache (instant).

        public List<LinkedElement> GetAllLinkedElements()
        {
            if (_elementCache != null) return _elementCache;

            _elementCache = new List<LinkedElement>();

            foreach (var link in GetLoadedLinks())
            {
                // ── STEP 6: Only scan the categories that actually matter ──
                // Removed: OST_Walls, OST_Ceilings, OST_Floors (large, usually not needed
                // in live scan — full scan uses them separately for structural checks).
                // These were causing the "too many categories" performance problem.
                var cats = GetLiveScanCategories();

                foreach (var cat in cats)
                {
                    try
                    {
                        var els = new FilteredElementCollector(link.LinkDoc)
                            .OfCategory(cat)
                            .WhereElementIsNotElementType()
                            .ToElements();

                        foreach (var el in els)
                        {
                            var bb = GetTransformedBB(el, link.Transform);
                            if (bb == null) continue;

                            _elementCache.Add(new LinkedElement
                            {
                                Element       = el,
                                SourceDoc     = link.LinkDoc,
                                LinkTransform = link.Transform,
                                LinkFileName  = link.FileName,
                                LinkDisc      = link.Discipline,
                                TransformedBB = bb
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(string.Concat(
                            "[ClashResolve] Category collect error: ", ex.Message));
                    }
                }

                Debug.WriteLine(string.Concat(
                    "[ClashResolve] Cached ", _elementCache.Count.ToString(),
                    " elements from [", link.FileName, "]"));
            }

            Debug.WriteLine(string.Concat(
                "[ClashResolve] Total linked element cache: ",
                _elementCache.Count.ToString()));

            return _elementCache;
        }

        // ── STEP 6: Minimal category set for live scan ────────────────────
        // Only the categories that actually cause MEP clashes.
        // Full scan uses a broader set through GetElementsFromLink().
        private static BuiltInCategory[] GetLiveScanCategories() => new[]
        {
            // Structural — the most important clash targets
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,

            // MEP — check between discipline models
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_Sprinklers,
        };

        // ── Transform bounding box into host coordinates ──────────────────
        public static BoundingBoxXYZ? GetTransformedBB(Element el, Transform transform)
        {
            try
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) return null;
                if (transform == null || transform.IsIdentity) return bb;

                var corners = new XYZ[]
                {
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                    new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                    new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                    new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z),
                };

                var tx = corners.Select(c => transform.OfPoint(c)).ToList();

                var result = new BoundingBoxXYZ();
                result.Min = new XYZ(tx.Min(p => p.X), tx.Min(p => p.Y), tx.Min(p => p.Z));
                result.Max = new XYZ(tx.Max(p => p.X), tx.Max(p => p.Y), tx.Max(p => p.Z));
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat(
                    "[ClashResolve] TransformBB error: ", ex.Message));
                return null;
            }
        }

        // ── Summary for UI ────────────────────────────────────────────────
        public string GetLinksSummary()
        {
            var links = GetAllLinks();
            if (!links.Any()) return "No linked models found in this project.";

            var lines = new List<string>();
            lines.Add(string.Concat("Found ", links.Count.ToString(), " linked model(s):\n"));

            foreach (var l in links)
            {
                string status = l.IsLoaded ? "✓ Loaded" : "✗ Not Loaded";
                lines.Add(string.Concat(
                    "  ", status, "  [", l.Discipline.ToString(), "]  ", l.FileName));
            }

            int unloaded = links.Count(l => !l.IsLoaded);
            if (unloaded > 0)
                lines.Add(string.Concat(
                    "\n⚠ ", unloaded.ToString(),
                    " link(s) not loaded. Use Manage → Manage Links to load them."));

            return string.Join("\n", lines);
        }

        // ── Discipline detection ──────────────────────────────────────────
        private static LinkDiscipline DetectDiscipline(Document doc, string fileName)
        {
            string fn = fileName.ToLowerInvariant();

            if (ContainsAny(fn, "struct", "str-", "-str", "s-", "framing", "concrete", "steel"))
                return LinkDiscipline.Structural;
            if (ContainsAny(fn, "arch", "arc-", "-arc", "a-", "architectural", "building"))
                return LinkDiscipline.Architectural;
            if (ContainsAny(fn, "mech", "hvac", "mechanical", "duct", "m-"))
                return LinkDiscipline.MEP_Mechanical;
            if (ContainsAny(fn, "elec", "electrical", "e-", "power", "lighting"))
                return LinkDiscipline.MEP_Electrical;
            if (ContainsAny(fn, "plumb", "plumbing", "p-", "sanitary", "drain"))
                return LinkDiscipline.MEP_Plumbing;
            if (ContainsAny(fn, "fire", "sprinkler", "fp-", "suppression"))
                return LinkDiscipline.MEP_FireProtection;
            if (ContainsAny(fn, "civil", "site", "topo"))
                return LinkDiscipline.Civil;

            return DetectByCount(doc);
        }

        private static LinkDiscipline DetectByCount(Document doc)
        {
            int structural = CountCat(doc, BuiltInCategory.OST_StructuralFraming)
                           + CountCat(doc, BuiltInCategory.OST_StructuralColumns);
            int walls      = CountCat(doc, BuiltInCategory.OST_Walls)
                           + CountCat(doc, BuiltInCategory.OST_Floors);
            int ducts      = CountCat(doc, BuiltInCategory.OST_DuctCurves);
            int pipes      = CountCat(doc, BuiltInCategory.OST_PipeCurves);
            int cable      = CountCat(doc, BuiltInCategory.OST_CableTray)
                           + CountCat(doc, BuiltInCategory.OST_Conduit);
            int sprinklers = CountCat(doc, BuiltInCategory.OST_Sprinklers);

            int max = Math.Max(structural, Math.Max(walls,
                      Math.Max(ducts, Math.Max(pipes,
                      Math.Max(cable, sprinklers)))));
            if (max == 0) return LinkDiscipline.Unknown;

            if (max == structural) return LinkDiscipline.Structural;
            if (max == walls)      return LinkDiscipline.Architectural;
            if (max == ducts)      return LinkDiscipline.MEP_Mechanical;
            if (max == pipes)      return LinkDiscipline.MEP_Plumbing;
            if (max == cable)      return LinkDiscipline.MEP_Electrical;
            return LinkDiscipline.MEP_FireProtection;
        }

        private static int CountCat(Document doc, BuiltInCategory cat)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType().GetElementCount();
            }
            catch { return 0; }
        }

        private static bool ContainsAny(string s, params string[] keywords) =>
            keywords.Any(k => s.Contains(k));

        // FIX: GetAbsolutePath().ToString() — correct API for ModelPath
        private static string GetLinkFileName(RevitLinkInstance inst)
        {
            try
            {
                var typeId   = inst.GetTypeId();
                var linkType = inst.Document.GetElement(typeId) as RevitLinkType;
                if (linkType != null)
                {
                    var absPath = linkType.GetExternalFileReference()
                                         .GetAbsolutePath().ToString();
                    if (!string.IsNullOrEmpty(absPath))
                        return Path.GetFileNameWithoutExtension(absPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat(
                    "[ClashResolve] GetLinkFileName error: ", ex.Message));
            }
            return inst.Name ?? "Unknown Link";
        }
    }
}
