// Engine/ClashEngine.cs  — v4.1
//
// FIX v4.1:
//   • RunSelectionScan / RunTargetedScan: spatial grid was always empty on a
//     new ClashEngine instance (created per-event in EventListener).
//     Fix: fall back to Revit BoundingBoxIntersectsFilter when grid is empty.
//     This makes the live monitor work with ZERO dependency on a prior full scan.
//
//   • RunFullScan: accepts optional levelId filter so single-floor scans work.
//
//   • ApplyLevelFilter: restricts element list to a specific level elevation band.

using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Links;
using ClashResolveAI.Rules;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.ClashEngine
{
    public class ClashEngine
    {
        private readonly Document            _doc;
        private readonly RulesEngine         _rules;
        private readonly LinkedModelManager  _links;
        private readonly GeometryCacheService _geoCache;

        private SpatialHashGrid _spatialGrid;

        private const double TargetRadiusFt    = 9.84;
        private const double MinIntersectVolume = 1e-9;
        private const int    BatchSize          = 500;
        private const int    MaxLiveCandidates  = 250;

        private readonly HashSet<string> _seenPairs = new HashSet<string>();

        public ClashEngine(Document doc, string ruleSetName = "DefaultRules")
        {
            _doc       = doc;
            _rules     = new RulesEngine(ruleSetName);
            _links     = new LinkedModelManager(doc);
            _geoCache  = GeometryCacheService.Instance;
            _spatialGrid = new SpatialHashGrid(cellSizeFt: 10.0);
        }

        // ════════════════════════════════════════════════════════════════
        //  FULL SCAN
        //  levelId: if not null/empty, restricts scan to that level only.
        // ════════════════════════════════════════════════════════════════

        public List<ClashResult> RunFullScan(
            bool includeLinks           = true,
            IProgress<string>? progress = null,
            CoordinationZone? zone      = null,
            string levelId              = "")   // NEW: single-floor filter
        {
            _seenPairs.Clear();
            var results = new List<ClashResult>();

            progress?.Report("Building geometry cache…");
            _geoCache.BuildCache(_doc, progress);

            progress?.Report("Building spatial index…");
            _spatialGrid.Clear();
            BuildSpatialIndex();
            progress?.Report(_spatialGrid.GetStats());

            progress?.Report("Running host model scan…");
            int id = 1;
            id = ScanHostVsHostSpatial(results, ref id, progress, zone, levelId);

            if (includeLinks)
            {
                var links = _links.GetLoadedLinks();
                if (links.Count > 0)
                {
                    progress?.Report($"Scanning {links.Count} linked model(s)…");
                    id = ScanHostVsLinks(results, ref id, links, progress, levelId);
                    id = ScanLinkVsLink(results, ref id, links, progress);
                }
            }

            return Finalise(results);
        }


        // ════════════════════════════════════════════════════════════════
        //  BUILD SPATIAL INDEX
        // ════════════════════════════════════════════════════════════════

        private void BuildSpatialIndex()
        {
            var disciplines = new[]
            {
                Discipline.HVAC, Discipline.Plumbing, Discipline.GravityDrainage,
                Discipline.Electrical, Discipline.FireProtection, Discipline.Structural,
                Discipline.MedicalGas, Discipline.CableTray, Discipline.Conduit
            };

            foreach (var disc in disciplines)
            {
                var elements = ElementCollector.GetByDiscipline(_doc, disc);
                foreach (var el in elements)
                {
                    var bb = _geoCache.Get(el.Id)?.BoundingBox ?? el.get_BoundingBox(null);
                    if (bb != null)
                        _spatialGrid.Register(el.Id, bb, disc);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  TARGETED SCAN  — live monitor (changed elements)
        //
        //  FIX v4.1: Grid empty → use BoundingBoxIntersectsFilter fallback.
        //  This means live monitor works even without a prior full scan.
        // ════════════════════════════════════════════════════════════════

        public List<ClashResult> RunTargetedScan(IEnumerable<ElementId> changedIds)
        {
            var results = new List<ClashResult>();
            int id      = 1;

            foreach (var eid in changedIds)
            {
                try
                {
                    var el = _doc.GetElement(eid);
                    if (el == null || !ElementCollector.IsMonitoredClashElement(el)) continue;

                    var disc = ElementCollector.GetDiscipline(el);
                    if (disc == Discipline.Unknown) continue;

                    var bb = el.get_BoundingBox(null);
                    if (bb == null) continue;

                    // Update spatial grid for modified element (if grid is built)
                    if (_spatialGrid.RegisteredCount > 0)
                        _spatialGrid.Update(eid, bb, disc);

                    _geoCache.InvalidateOne(eid);

                    var targetDiscs = GetTargetDisciplines(disc);
                    var candidateIds = GetCandidates(bb, targetDiscs, MaxLiveCandidates);

                    foreach (var candidateId in candidateIds)
                    {
                        if (candidateId == eid) continue;
                        var tgt = _doc.GetElement(candidateId);
                        if (tgt == null) continue;

                        var tgtDisc = ElementCollector.GetDiscipline(tgt);
                        if (tgtDisc == Discipline.Unknown) continue;

                        string rk = GetRuleKey(disc, tgtDisc);
                        var c = CheckPairLightweight(id, el, disc, tgt, tgtDisc, rk);
                        if (c != null) { results.Add(c); id++; }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClashEngine] Targeted scan error: {ex.Message}");
                }
            }

            return DeduplicateAndFinish(results);
        }

        // ════════════════════════════════════════════════════════════════
        //  SELECTION SCAN  — instant feedback on MEP element selection
        //
        //  FIX v4.1: Grid empty → use BoundingBoxIntersectsFilter fallback.
        // ════════════════════════════════════════════════════════════════

        public List<ClashResult> RunSelectionScan(Element selected)
        {
            var results = new List<ClashResult>();
            if (!ElementCollector.IsMonitoredClashElement(selected)) return results;

            var disc = ElementCollector.GetDiscipline(selected);
            if (disc == Discipline.Unknown) return results;

            var bb = selected.get_BoundingBox(null);
            if (bb == null) return results;

            var targetDiscs  = GetTargetDisciplines(disc);
            var candidateIds = GetCandidates(bb, targetDiscs, MaxLiveCandidates);

            int id = 1;
            foreach (var cid in candidateIds)
            {
                if (cid == selected.Id) continue;
                var tgt = _doc.GetElement(cid);
                if (tgt == null) continue;

                var tgtDisc = ElementCollector.GetDiscipline(tgt);
                if (tgtDisc == Discipline.Unknown) continue;

                string rk = GetRuleKey(disc, tgtDisc);
                var c = CheckPairLightweight(id, selected, disc, tgt, tgtDisc, rk);
                if (c != null) { results.Add(c); id++; }
            }

            return DeduplicateAndFinish(results);
        }

        // ════════════════════════════════════════════════════════════════
        //  GET CANDIDATES
        //  Returns candidate element IDs near the given bounding box.
        //  Uses spatial grid when populated; falls back to Revit's native
        //  BoundingBoxIntersectsFilter when the grid is empty.
        //  The fallback ensures live monitor works without a prior full scan.
        // ════════════════════════════════════════════════════════════════

        private List<ElementId> GetCandidates(
            BoundingBoxXYZ bb,
            IEnumerable<Discipline> targetDiscs,
            int maxResults = int.MaxValue)
        {
            if (_spatialGrid.RegisteredCount > 0)
            {
                // Grid built — O(1) spatial lookup
                return _spatialGrid.QueryNearbyByDiscipline(bb, targetDiscs, TargetRadiusFt);
            }

            // Grid empty — fall back to Revit native spatial filter
            // Expand the bounding box by the target radius
            var expanded = Expand(bb, TargetRadiusFt);
            var outline  = new Outline(expanded.Min, expanded.Max);
            var bbFilter = new BoundingBoxIntersectsFilter(outline);

            var result = new List<ElementId>();
            foreach (var disc in targetDiscs)
            {
                try
                {
                    var cats = ElementCollector.GetCategoriesForDiscipline(disc);
                    foreach (var cat in cats)
                    {
                        var els = new FilteredElementCollector(_doc)
                            .OfCategory(cat)
                            .WherePasses(bbFilter)
                            .WhereElementIsNotElementType()
                            .ToElementIds();
                        result.AddRange(els);
                        if (result.Count >= maxResults)
                            return result.Distinct().Take(maxResults).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClashEngine] Candidate fallback error: {ex.Message}");
                }
            }
            return result.Distinct().Take(maxResults).ToList();
        }


        // ════════════════════════════════════════════════════════════════
        //  SELECTION SCAN WITH LINKS  — v5.1
        //  Same as RunSelectionScan but also checks linked model structural.
        //  This is the FIX for BUG 3 (linked structural not found).
        // ════════════════════════════════════════════════════════════════

        public List<ClashResult> RunSelectionScanWithLinks(Element selected)
        {
            // Host-model scan (existing logic)
            var results = RunSelectionScan(selected);

            // Linked structural scan (NEW)
            if (!ElementCollector.IsMonitoredClashElement(selected)) return results;
            var disc = ElementCollector.GetDiscipline(selected);
            if (disc == Discipline.Unknown) return results;
            var targetDiscs = GetTargetDisciplines(disc);
            if (!targetDiscs.Contains(Discipline.Structural)) return results;

            var bb = selected.get_BoundingBox(null);
            if (bb == null) return results;

            int id = results.Count + 1;
            var linked = ScanHostAgainstLinkedStructural(ref id, selected, disc, bb);
            results.AddRange(linked);
            return DeduplicateAndFinish(results);
        }

        // ════════════════════════════════════════════════════════════════
        //  TARGETED SCAN WITH LINKS  — v5.1
        //  Same as RunTargetedScan but also checks linked model structural.
        // ════════════════════════════════════════════════════════════════

        public List<ClashResult> RunTargetedScanWithLinks(IEnumerable<ElementId> changedIds)
        {
            // Host-model scan (existing logic)
            var results = RunTargetedScan(changedIds);

            // For each changed element also scan against linked structural
            int id = results.Count + 1;
            foreach (var eid in changedIds)
            {
                try
                {
                    var el = _doc.GetElement(eid);
                    if (!ElementCollector.IsMonitoredClashElement(el)) continue;
                    var disc = ElementCollector.GetDiscipline(el);
                    if (disc == Discipline.Unknown) continue;
                    var targetDiscs = GetTargetDisciplines(disc);
                    if (!targetDiscs.Contains(Discipline.Structural)) continue;
                    var bb = el.get_BoundingBox(null);
                    if (bb == null) continue;

                    var linked = ScanHostAgainstLinkedStructural(ref id, el, disc, bb);
                    results.AddRange(linked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ClashEngine] TargetedWithLinks: {ex.Message}");
                }
            }
            return DeduplicateAndFinish(results);
        }

        // ════════════════════════════════════════════════════════════════
        //  SCAN HOST ELEMENT vs LINKED STRUCTURAL  — v5.1 (private)
        //
        //  Queries every loaded linked document for structural elements
        //  whose TRANSFORMED bounding box is near the host element's BB.
        //  Uses CheckPairCrossDoc so linked transforms are applied correctly.
        //
        //  Key difference from full-scan ScanHostVsLinks:
        //    • No solid Boolean on the live path (AABB only for speed)
        //    • Limited to structural target discipline
        //    • MaxLiveCandidates cap per link
        // ════════════════════════════════════════════════════════════════

        private List<ClashResult> ScanHostAgainstLinkedStructural(
            ref int id, Element hostEl, Discipline hostDisc, BoundingBoxXYZ hostBB)
        {
            var results  = new List<ClashResult>();
            var links    = _links.GetLoadedLinks();
            if (!links.Any()) return results;

            string hostRuleKey = GetRuleKey(hostDisc, Discipline.Structural);
            // Expanded BB for proximity check
            var expanded = Expand(hostBB, TargetRadiusFt);

            foreach (var link in links)
            {
                if (link.LinkDoc == null) continue;

                var structCats = ElementCollector.GetCategoriesForDiscipline(Discipline.Structural);
                int candidateCount = 0;

                foreach (var cat in structCats)
                {
                    if (candidateCount >= MaxLiveCandidates) break;
                    try
                    {
                        // Build an outline in the LINKED document's coordinate space
                        // by applying the inverse transform to the expanded BB
                        Transform inv = link.Transform.Inverse;
                        XYZ linkMin = inv.OfPoint(expanded.Min);
                        XYZ linkMax = inv.OfPoint(expanded.Max);
                        // Re-order because inverse transform can swap min/max
                        var safeMin = new XYZ(
                            System.Math.Min(linkMin.X, linkMax.X),
                            System.Math.Min(linkMin.Y, linkMax.Y),
                            System.Math.Min(linkMin.Z, linkMax.Z));
                        var safeMax = new XYZ(
                            System.Math.Max(linkMin.X, linkMax.X),
                            System.Math.Max(linkMin.Y, linkMax.Y),
                            System.Math.Max(linkMin.Z, linkMax.Z));

                        var outline  = new Outline(safeMin, safeMax);
                        var bbFilter = new BoundingBoxIntersectsFilter(outline);

                        var linkEls = new FilteredElementCollector(link.LinkDoc)
                            .OfCategory(cat)
                            .WherePasses(bbFilter)
                            .WhereElementIsNotElementType()
                            .ToElements();

                        foreach (var linkEl in linkEls)
                        {
                            if (candidateCount >= MaxLiveCandidates) break;

                            var linkBB = LinkedModelManager.GetTransformedBB(linkEl, link.Transform);
                            if (linkBB == null) continue;

                            // Quick proximity gate before full check
                            if (!AABBOverlap(expanded, linkBB)) continue;

                            string pk = NormalizedPairKey(hostEl.Id, linkEl.Id);
                            if (_seenPairs.Contains(pk)) continue;

                            var c = CheckPairCrossDoc(id, hostEl, hostDisc, hostBB,
                                linkEl, Discipline.Structural, linkBB,
                                link.Transform, hostRuleKey,
                                "", link.FileName, true);

                            if (c != null)
                            {
                                _seenPairs.Add(pk);
                                results.Add(c);
                                id++;
                            }
                            candidateCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ClashEngine] LinkedStructural [{link.FileName}]: {ex.Message}");
                    }
                }
            }
            return results;
        }

        // ════════════════════════════════════════════════════════════════
        //  DEEP VALIDATE  — full Boolean check on confirmed candidate
        // ════════════════════════════════════════════════════════════════

        public ClashResult? DeepValidate(ClashResult candidate)
        {
            if (candidate?.ElementA == null || candidate.ElementB == null) return null;
            try
            {
                var solidA = ElementCollector.GetSolidFromElement(candidate.ElementA);
                var solidB = ElementCollector.GetSolidFromElement(candidate.ElementB);
                if (solidA == null || solidB == null) return candidate;

                Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                    solidA, solidB, BooleanOperationsType.Intersect);

                if (intersection != null && intersection.Volume > MinIntersectVolume)
                {
                    candidate.Severity         = ClashSeverity.Hard;
                    candidate.OverlapVolumeMM3 = intersection.Volume * 304.8 * 304.8 * 304.8;
                    candidate.TestType         = ClashTestType.HardClash;
                    return candidate;
                }
                else
                {
                    if (candidate.Severity == ClashSeverity.Hard)
                        candidate.Severity = ClashSeverity.Soft;
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] DeepValidate: {ex.Message}");
                return candidate;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  HOST vs HOST SCAN  — spatial grid, batched
        // ════════════════════════════════════════════════════════════════

        private int ScanHostVsHostSpatial(
            List<ClashResult> results, ref int id,
            IProgress<string>? progress,
            CoordinationZone? zone,
            string levelId)
        {
            foreach (var entry in ElementCollector.ClashMatrix)
            {
                if (!entry.Check) continue;

                var srcAll = ElementCollector.GetByDiscipline(_doc, entry.Source);
                if (srcAll.Count == 0) continue;

                // ── Level filter (FIX Issue 1) ─────────────────────────
                if (!string.IsNullOrEmpty(levelId))
                    srcAll = FilterByLevel(srcAll, levelId);
                else if (zone != null)
                    srcAll = ApplyZoneFilter(srcAll, zone);

                if (srcAll.Count == 0) continue;

                int batches = (srcAll.Count + BatchSize - 1) / BatchSize;
                for (int b = 0; b < batches; b++)
                {
                    var batch = srcAll.Skip(b * BatchSize).Take(BatchSize).ToList();
                    progress?.Report(
                        $"[Host↔Host] {entry.Source} vs {entry.Target} — " +
                        $"batch {b + 1}/{batches} ({batch.Count} elements)…");

                    foreach (var src in batch)
                    {
                        var bbA = _geoCache.Get(src.Id)?.BoundingBox ?? src.get_BoundingBox(null);
                        if (bbA == null) continue;

                        double clearFt   = _rules.GetClearanceFt(entry.Source, entry.Target, entry.RuleKey);
                        var    candidates = GetCandidates(bbA, new[] { entry.Target });

                        foreach (var cid in candidates)
                        {
                            if (cid == src.Id) continue;
                            string pairKey = NormalizedPairKey(src.Id, cid);
                            if (_seenPairs.Contains(pairKey)) continue;

                            var tgt = _doc.GetElement(cid);
                            if (tgt == null) continue;

                            var c = CheckPairFull(id, src, entry.Source, tgt, entry.Target,
                                entry.RuleKey, "", "");
                            if (c != null)
                            {
                                _seenPairs.Add(pairKey);
                                results.Add(c);
                                id++;
                            }
                        }
                    }
                }
            }
            return id;
        }

        // ════════════════════════════════════════════════════════════════
        //  HOST vs LINKS
        // ════════════════════════════════════════════════════════════════

        private int ScanHostVsLinks(
            List<ClashResult> results, ref int id,
            List<LinkedModel> links, IProgress<string>? progress,
            string levelId)
        {
            var hostDiscs = new[] {
                Discipline.HVAC, Discipline.Plumbing, Discipline.Electrical,
                Discipline.FireProtection, Discipline.GravityDrainage, Discipline.MedicalGas
            };

            foreach (var hostDisc in hostDiscs)
            {
                var hostEls = ElementCollector.GetByDiscipline(_doc, hostDisc);
                if (!string.IsNullOrEmpty(levelId))
                    hostEls = FilterByLevel(hostEls, levelId);
                if (hostEls.Count == 0) continue;

                foreach (var link in links)
                {
                    var linkDisc = MapLinkDiscipline(link.Discipline);
                    if (linkDisc == Discipline.Unknown) continue;
                    if (hostDisc == linkDisc && linkDisc != Discipline.Structural) continue;

                    string rk      = GetRuleKey(hostDisc, linkDisc);
                    var    linkEls = GetElementsFromLink(link, linkDisc);
                    if (linkEls.Count == 0) continue;

                    progress?.Report($"[Host↔Link] {hostDisc} vs [{link.FileName}] {linkDisc}");

                    foreach (var src in hostEls)
                    {
                        var bbA = _geoCache.Get(src.Id)?.BoundingBox ?? src.get_BoundingBox(null);
                        if (bbA == null) continue;

                        foreach (var tgt in linkEls)
                        {
                            var bbB = LinkedModelManager.GetTransformedBB(tgt, link.Transform);
                            if (bbB == null) continue;
                            if (!AABBOverlap(Expand(bbA, TargetRadiusFt), bbB)) continue;

                            string pk = NormalizedPairKey(src.Id, tgt.Id);
                            if (_seenPairs.Contains(pk)) continue;

                            var c = CheckPairCrossDoc(id, src, hostDisc, bbA,
                                tgt, linkDisc, bbB, link.Transform, rk,
                                link.FileName, link.FileName);
                            if (c != null) { _seenPairs.Add(pk); results.Add(c); id++; }
                        }
                    }
                }
            }
            return id;
        }

        // ════════════════════════════════════════════════════════════════
        //  LINK vs LINK
        // ════════════════════════════════════════════════════════════════

        private int ScanLinkVsLink(
            List<ClashResult> results, ref int id,
            List<LinkedModel> links, IProgress<string>? progress)
        {
            var pairs = new[]
            {
                (LinkDiscipline.MEP_Plumbing,       LinkDiscipline.Structural),
                (LinkDiscipline.MEP_Mechanical,     LinkDiscipline.Structural),
                (LinkDiscipline.MEP_Electrical,     LinkDiscipline.Structural),
                (LinkDiscipline.MEP_FireProtection, LinkDiscipline.Structural),
                (LinkDiscipline.MEP_Plumbing,       LinkDiscipline.Architectural),
                (LinkDiscipline.MEP_Mechanical,     LinkDiscipline.Architectural),
            };

            foreach (var (ldA, ldB) in pairs)
            {
                var linkA = links.FirstOrDefault(l => l.Discipline == ldA);
                var linkB = links.FirstOrDefault(l => l.Discipline == ldB);
                if (linkA == null || linkB == null) continue;

                var mepA = MapLinkDiscipline(ldA);
                var mepB = MapLinkDiscipline(ldB);
                string rk = GetRuleKey(mepA, mepB);

                var elsA = GetElementsFromLink(linkA, mepA);
                var elsB = GetElementsFromLink(linkB, mepB);
                if (elsA.Count == 0 || elsB.Count == 0) continue;

                progress?.Report($"[Link↔Link] [{linkA.FileName}] vs [{linkB.FileName}]");

                foreach (var elA in elsA)
                {
                    var bbA = LinkedModelManager.GetTransformedBB(elA, linkA.Transform);
                    if (bbA == null) continue;

                    foreach (var elB in elsB)
                    {
                        var bbB = LinkedModelManager.GetTransformedBB(elB, linkB.Transform);
                        if (bbB == null) continue;
                        if (!AABBOverlap(Expand(bbA, TargetRadiusFt), bbB)) continue;

                        string pk = NormalizedPairKey(elA.Id, elB.Id);
                        if (_seenPairs.Contains(pk)) continue;

                        var c = CheckPairCrossDoc(id, elA, mepA, bbA, elB, mepB, bbB,
                            linkB.Transform, rk, linkA.FileName, linkB.FileName);
                        if (c != null) { _seenPairs.Add(pk); results.Add(c); id++; }
                    }
                }
            }
            return id;
        }

        // ════════════════════════════════════════════════════════════════
        //  CHECK PAIR — LIGHTWEIGHT  (Stage 1+2 only, no Booleans)
        // ════════════════════════════════════════════════════════════════

        private ClashResult? CheckPairLightweight(
            int id, Element elA, Discipline dA,
            Element elB, Discipline dB, string ruleKey)
        {
            try
            {
                var bbA = _geoCache.Get(elA.Id)?.BoundingBox ?? elA.get_BoundingBox(null);
                var bbB = _geoCache.Get(elB.Id)?.BoundingBox ?? elB.get_BoundingBox(null);
                if (bbA == null || bbB == null) return null;

                double clearFt = _rules.GetClearanceFt(dA, dB, ruleKey);
                double expand  = Math.Max(clearFt, 0.164);

                if (!AABBOverlap(Expand(bbA, expand), bbB)) return null;

                double gapMm = ComputeGapMm(bbA, bbB);

                ClashSeverity sev;
                if      (gapMm < 0)                  sev = ClashSeverity.Hard;
                else if (gapMm < clearFt * 304.8)    sev = ClashSeverity.Soft;
                else if (gapMm < 0.328 * 304.8)      sev = ClashSeverity.Clearance;
                else return null;

                return BuildResult(id, elA, dA, elB, dB, ruleKey,
                    sev, gapMm, bbA, bbB, "", "", 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] Lightweight check: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  CHECK PAIR — FULL  (4-stage pipeline with deferred Boolean)
        // ════════════════════════════════════════════════════════════════

        private ClashResult? CheckPairFull(
            int id,
            Element elA, Discipline dA,
            Element elB, Discipline dB,
            string ruleKey, string linkFileA, string linkFileB)
        {
            try
            {
                var bbA = _geoCache.Get(elA.Id)?.BoundingBox ?? elA.get_BoundingBox(null);
                var bbB = _geoCache.Get(elB.Id)?.BoundingBox ?? elB.get_BoundingBox(null);
                if (bbA == null || bbB == null) return null;

                double clearFt = _rules.GetClearanceFt(dA, dB, ruleKey);
                double expand  = clearFt + 0.164;

                // Stage 1
                if (!AABBOverlap(Expand(bbA, expand), bbB)) return null;

                // Stage 2
                double gapMm = ComputeGapMm(bbA, bbB);
                if (gapMm > clearFt * 304.8 + 100) return null;

                // Stage 3
                bool tier3Pass = false;
                try { tier3Pass = new ElementIntersectsElementFilter(elB).PassesFilter(elA); }
                catch (Exception ex) { Debug.WriteLine($"[ClashEngine] Tier3: {ex.Message}"); }

                // Stage 4 — Boolean deferred, only when Stage 3 passes
                double overlapVolMM3 = 0;
                bool   solidClash    = false;

                if (tier3Pass)
                {
                    var solidA = ElementCollector.GetSolidFromElement(elA);
                    var solidB = ElementCollector.GetSolidFromElement(elB);

                    if (solidA != null && solidB != null)
                    {
                        try
                        {
                            Solid ix = BooleanOperationsUtils.ExecuteBooleanOperation(
                                solidA, solidB, BooleanOperationsType.Intersect);
                            if (ix != null && ix.Volume > MinIntersectVolume)
                            {
                                solidClash    = true;
                                overlapVolMM3 = ix.Volume * 304.8 * 304.8 * 304.8;
                            }
                            else tier3Pass = false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ClashEngine] Boolean: {ex.Message}");
                            solidClash = tier3Pass;
                        }
                    }
                    else solidClash = tier3Pass;
                }

                ClashSeverity sev;
                if      (solidClash || (tier3Pass && gapMm < 0)) sev = ClashSeverity.Hard;
                else if (gapMm < 0)                              sev = ClashSeverity.Hard;
                else if (gapMm < clearFt * 304.8)               sev = ClashSeverity.Soft;
                else if (gapMm < 0.328 * 304.8)                 sev = ClashSeverity.Clearance;
                else return null;

                return BuildResult(id, elA, dA, elB, dB, ruleKey, sev, gapMm,
                    bbA, bbB, linkFileA, linkFileB, overlapVolMM3);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] CheckPairFull: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  CHECK PAIR — CROSS-DOC
        // ════════════════════════════════════════════════════════════════

        private ClashResult? CheckPairCrossDoc(
            int id,
            Element elA, Discipline dA, BoundingBoxXYZ bbA,
            Element elB, Discipline dB, BoundingBoxXYZ bbB,
            Transform linkTransform, string ruleKey,
            string linkFileA, string linkFileB,
            bool lightweight = false)
        {
            try
            {
                double clearFt = _rules.GetClearanceFt(dA, dB, ruleKey);
                if (!AABBOverlap(Expand(bbA, clearFt + 0.164), bbB)) return null;

                double gapMm       = ComputeGapMm(bbA, bbB);
                bool   aabbOverlap = AABBOverlap(bbA, bbB);
                double overlapVol  = 0;
                bool   solidClash  = false;

                if (aabbOverlap && !lightweight)
                {
                    var solidA = ElementCollector.GetSolidFromElement(elA);
                    var solidB = ElementCollector.GetSolidFromElement(elB);

                    if (solidA != null && solidB != null && !linkTransform.IsIdentity)
                    {
                        try
                        {
                            Solid solidBT = SolidUtils.CreateTransformed(solidB, linkTransform);
                            Solid ix = BooleanOperationsUtils.ExecuteBooleanOperation(
                                solidA, solidBT, BooleanOperationsType.Intersect);
                            if (ix != null && ix.Volume > MinIntersectVolume)
                            {
                                solidClash = true;
                                overlapVol = ix.Volume * 304.8 * 304.8 * 304.8;
                            }
                            else aabbOverlap = false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ClashEngine] CrossDoc Boolean: {ex.Message}");
                            solidClash = aabbOverlap;
                        }
                    }
                    else solidClash = aabbOverlap;
                }
                else if (aabbOverlap && lightweight)
                {
                    solidClash = true; // For lightweight, AABB overlap is treated as solid clash
                }

                ClashSeverity sev;
                if      (solidClash || (aabbOverlap && gapMm < 0)) sev = ClashSeverity.Hard;
                else if (gapMm < clearFt * 304.8)                  sev = ClashSeverity.Soft;
                else if (gapMm < 0.328 * 304.8)                    sev = ClashSeverity.Clearance;
                else return null;

                return BuildResult(id, elA, dA, elB, dB, ruleKey, sev, gapMm,
                    bbA, bbB, linkFileA, linkFileB, overlapVol);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] CrossDoc: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD RESULT
        // ════════════════════════════════════════════════════════════════

        private ClashResult? BuildResult(
            int id,
            Element elA, Discipline dA, Element elB, Discipline dB,
            string ruleKey, ClashSeverity sev, double gapMm,
            BoundingBoxXYZ bbA, BoundingBoxXYZ bbB,
            string linkFileA, string linkFileB, double overlapVolMM3)
        {
            var (finalSev, ruleApplied) = _rules.Evaluate(elA, elB, dA, dB, ruleKey, sev);
            if (finalSev == ClashSeverity.Ignore) return null;

            var    pt     = GetClashPoint(bbA, bbB);
            double toM(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
            string linkInfo = string.IsNullOrEmpty(linkFileA)
                ? "" : $" [Link:{linkFileA}]";
            string volInfo = overlapVolMM3 > 0 ? $" | Vol:{overlapVolMM3:F1}mm³" : "";

            Discipline mover = PriorityMatrix.GetMovingDiscipline(dA, dB);

            return new ClashResult
            {
                ElementA          = elA,
                ElementB          = elB,
                DisciplineA       = dA,
                DisciplineB       = dB,
                SystemTypeA       = GetSystemType(elA),
                SystemTypeB       = GetSystemType(elB),
                ClashType         = ruleKey,
                TestType          = overlapVolMM3 > 0 ? ClashTestType.HardClash : ClashTestType.ClearanceClash,
                Severity          = finalSev,
                GapMM             = Math.Round(gapMm, 1),
                OverlapVolumeMM3  = Math.Round(overlapVolMM3, 1),
                ClashPoint        = pt,
                LocationText      = $"X:{toM(pt.X):F2}m  Y:{toM(pt.Y):F2}m  Z:{toM(pt.Z):F2}m{linkInfo}{volInfo}",
                LevelName         = GetLevel(pt),
                GridRef           = GetGrid(pt),
                RuleApplied       = ruleApplied,
                Priority          = AssignPriority(finalSev, dA, dB),
                MovingDiscipline  = mover.ToString(),
                LinkFileA         = linkFileA,
                LinkFileB         = linkFileB,
                Status            = ClashStatus.New
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  LEVEL FILTER  — restricts elements to a specific level
        // ════════════════════════════════════════════════════════════════

        private List<Element> FilterByLevel(List<Element> elements, string levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return elements;
            try
            {
                var level = _doc.GetElement(new ElementId(int.Parse(levelId))) as Level;
                if (level == null) return elements;

                // Get the next level elevation for the upper bound
                double levelElev = level.Elevation;
                double nextElev  = GetNextLevelElevation(levelElev);
                double bandFt    = 3.28; // 1m tolerance above and below

                return elements.Where(el =>
                {
                    try
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) return false;
                        double midZ = (bb.Min.Z + bb.Max.Z) / 2.0;
                        return midZ >= (levelElev - bandFt) && midZ < (nextElev + bandFt);
                    }
                    catch { return false; }
                }).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashEngine] FilterByLevel: {ex.Message}");
                return elements;
            }
        }

        private double GetNextLevelElevation(double currentElev)
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var next = levels.FirstOrDefault(l => l.Elevation > currentElev + 0.1);
            return next?.Elevation ?? (currentElev + 16.4); // default 5m if no next level
        }

        private List<Element> ApplyZoneFilter(List<Element> elements, CoordinationZone zone)
        {
            if (zone == null) return elements;
            var result = new List<Element>(elements.Count);
            foreach (var el in elements)
            {
                try
                {
                    if (!string.IsNullOrEmpty(zone.LevelName))
                    {
                        var bb = el.get_BoundingBox(null);
                        if (bb == null) continue;
                        if (!GetLevel(GetClashPoint(bb, bb)).Equals(
                            zone.LevelName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    result.Add(el);
                }
                catch { result.Add(el); }
            }
            return result;
        }

        // ════════════════════════════════════════════════════════════════
        //  GEOMETRY HELPERS
        // ════════════════════════════════════════════════════════════════

        private static bool AABBOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
            a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
            a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

        private static BoundingBoxXYZ Expand(BoundingBoxXYZ bb, double ft)
        {
            var e = new BoundingBoxXYZ();
            e.Min = new XYZ(bb.Min.X - ft, bb.Min.Y - ft, bb.Min.Z - ft);
            e.Max = new XYZ(bb.Max.X + ft, bb.Max.Y + ft, bb.Max.Z + ft);
            return e;
        }

        private static double ComputeGapMm(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double gx = Math.Max(a.Min.X, b.Min.X) - Math.Min(a.Max.X, b.Max.X);
            double gy = Math.Max(a.Min.Y, b.Min.Y) - Math.Min(a.Max.Y, b.Max.Y);
            double gz = Math.Max(a.Min.Z, b.Min.Z) - Math.Min(a.Max.Z, b.Max.Z);
            return Math.Max(gx, Math.Max(gy, gz)) * 304.8;
        }

        private static XYZ GetClashPoint(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            new XYZ(
                (Math.Max(a.Min.X, b.Min.X) + Math.Min(a.Max.X, b.Max.X)) / 2,
                (Math.Max(a.Min.Y, b.Min.Y) + Math.Min(a.Max.Y, b.Max.Y)) / 2,
                (Math.Max(a.Min.Z, b.Min.Z) + Math.Min(a.Max.Z, b.Max.Z)) / 2);

        private List<Level>? _levelCache;
        private List<Level> GetSortedLevels()
        {
            if (_levelCache != null) return _levelCache;
            _levelCache = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            return _levelCache;
        }

        private string GetLevel(XYZ pt)
        {
            Level? best = null;
            foreach (var lv in GetSortedLevels())
                if (lv.Elevation <= pt.Z + 1.0) best = lv; else break;
            return best?.Name ?? "Unknown Level";
        }

        private string GetGrid(XYZ pt)
        {
            var grids = new FilteredElementCollector(_doc)
                .OfClass(typeof(Grid)).Cast<Grid>().ToList();
            if (!grids.Any()) return "N/A";
            string? h = null, v = null;
            double dh = double.MaxValue, dv = double.MaxValue;
            foreach (var g in grids)
            {
                if (!(g.Curve is Line ln)) continue;
                var dir = (ln.GetEndPoint(1) - ln.GetEndPoint(0)).Normalize();
                bool isV = Math.Abs(dir.X) < 0.1;
                double dist = ln.Project(pt).Distance;
                if (isV  && dist < dv) { dv = dist; v = g.Name; }
                if (!isV && dist < dh) { dh = dist; h = g.Name; }
            }
            return $"{v ?? "?"}/{h ?? "?"}";
        }

        private static string AssignPriority(ClashSeverity s, Discipline a, Discipline b)
        {
            if (s == ClashSeverity.Critical) return "Critical";
            if (s == ClashSeverity.Hard)
                return (a == Discipline.Structural || b == Discipline.Structural ||
                        a == Discipline.GravityDrainage || b == Discipline.GravityDrainage)
                    ? "Critical" : "High";
            return s == ClashSeverity.Soft ? "Medium" : "Low";
        }

        private static string GetSystemType(Element el)
        {
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)
                     ?? el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                return p?.AsString() ?? "";
            }
            catch { return ""; }
        }

        private static string NormalizedPairKey(ElementId a, ElementId b)
        {
            long la = a.Value, lb = b.Value;
            return $"{Math.Min(la, lb)}-{Math.Max(la, lb)}";
        }

        private static List<Discipline> GetTargetDisciplines(Discipline disc) =>
            ElementCollector.ClashMatrix
                .Where(m => m.Source == disc || m.Target == disc)
                .Select(m => m.Source == disc ? m.Target : m.Source)
                .Distinct().ToList();

        private static List<ClashResult> DeduplicateAndFinish(List<ClashResult> results)
        {
            var seen   = new HashSet<string>();
            var deduped = new List<ClashResult>();
            foreach (var c in results)
                if (seen.Add(c.NormalizedKey))
                    deduped.Add(c);
            return Finalise(deduped);
        }

        private static List<ClashResult> Finalise(List<ClashResult> results) =>
            results.Where(c => c.Severity != ClashSeverity.Ignore)
                   .OrderBy(c => (int)c.Severity)
                   .ThenBy(c => c.Priority)
                   .ToList();

        private static Discipline MapLinkDiscipline(LinkDiscipline ld)
        {
            switch (ld)
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

        private List<Element> GetElementsFromLink(LinkedModel link, Discipline disc)
        {
            var cats   = ElementCollector.GetCategoriesForDiscipline(disc);
            var result = new List<Element>();
            foreach (var cat in cats)
            {
                try
                {
                    result.AddRange(new FilteredElementCollector(link.LinkDoc)
                        .OfCategory(cat).WhereElementIsNotElementType().ToElements());
                }
                catch { }
            }
            return result;
        }

        private static string GetRuleKey(Discipline a, Discipline b)
        {
            var entry = ElementCollector.ClashMatrix.FirstOrDefault(m => 
                (m.Source == a && m.Target == b) || (m.Source == b && m.Target == a));
            return entry?.RuleKey ?? "Generic";
        }
    }
}
