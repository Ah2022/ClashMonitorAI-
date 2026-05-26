// Core/GeometryCacheService.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #2: Geometry Cache
//
// Navisworks preprocesses geometry ONCE and never repeats extraction.
// This service replicates that behaviour:
//
//   1. At startup / full scan:  BuildCache() processes all MEP elements
//      — extracts Solid, BoundingBox, Center, ClearanceBox, Discipline
//      — stores keyed by ElementId.Value
//
//   2. During live monitor:  Get(elementId) returns cached data instantly
//      — no get_Geometry() call, no Options object, no ViewDetailLevel
//
//   3. On DocumentChanged:  Invalidate(elementIds) marks stale entries
//      — background worker refreshes stale entries on next idle cycle
//
// PERFORMANCE GAIN:
//   Before: get_Geometry(Options{Fine}) called on EVERY element, EVERY scan
//   After:  Dictionary lookup — O(1), near-zero cost

using Autodesk.Revit.DB;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ClashResolveAI.Core
{
    /// <summary>
    /// Thread-safe geometry cache for all MEP elements.
    /// Eliminates repeated get_Geometry() calls during clash detection.
    /// </summary>
    public class GeometryCacheService
    {
        // ── Singleton ────────────────────────────────────────────────────
        private static GeometryCacheService? _instance;
        public  static GeometryCacheService   Instance =>
            _instance ?? (_instance = new GeometryCacheService());

        // ── Cache storage ─────────────────────────────────────────────────
        // ConcurrentDictionary for thread-safe access
        private readonly ConcurrentDictionary<long, CachedGeometry> _cache =
            new ConcurrentDictionary<long, CachedGeometry>();

        // ── Stale set — elements needing refresh ──────────────────────────
        private readonly ConcurrentDictionary<long, bool> _stale =
            new ConcurrentDictionary<long, bool>();

        // ── Geometry options — MEDIUM detail only (never FINE in live) ────
        private static readonly Options _geomOptsLive = new Options
        {
            DetailLevel          = ViewDetailLevel.Medium,
            ComputeReferences    = false,
            IncludeNonVisibleObjects = false
        };

        private static readonly Options _geomOptsFull = new Options
        {
            DetailLevel          = ViewDetailLevel.Fine,
            ComputeReferences    = false,
            IncludeNonVisibleObjects = false
        };

        private const double MinSolidVolume = 1e-9;

        public int CachedCount  => _cache.Count;
        public int StaleCount   => _stale.Count;

        // ════════════════════════════════════════════════════════════════
        //  BUILD CACHE  — called once at full scan startup
        //  Processes all MEP elements and populates the cache.
        //  Returns count of successfully cached elements.
        // ════════════════════════════════════════════════════════════════

        public int BuildCache(Document doc, IProgress<string>? progress = null)
        {
            _cache.Clear();
            _stale.Clear();

            var disciplines = new[]
            {
                Discipline.HVAC, Discipline.Plumbing, Discipline.GravityDrainage,
                Discipline.Electrical, Discipline.FireProtection, Discipline.Structural,
                Discipline.MedicalGas, Discipline.CableTray, Discipline.Conduit
            };

            int count = 0;
            foreach (var disc in disciplines)
            {
                var elements = ElementCollector.GetRawByDisciplineAll(doc, disc);
                progress?.Report($"Caching {disc}: {elements.Count} elements…");

                foreach (var el in elements)
                {
                    try
                    {
                        var cached = BuildEntry(el, disc, useFineDetail: true);
                        if (cached != null)
                        {
                            _cache[el.Id.Value] = cached;
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GeoCache] Build error {el.Id.Value}: {ex.Message}");
                    }
                }
            }

            progress?.Report($"Geometry cache built: {count} elements cached.");
            return count;
        }

        // ════════════════════════════════════════════════════════════════
        //  GET  — retrieve cached geometry (O(1))
        //  Returns null if not cached; caller should fall back to live extraction.
        // ════════════════════════════════════════════════════════════════

        public CachedGeometry? Get(ElementId id)
        {
            if (id == null) return null;
            _cache.TryGetValue(id.Value, out var cached);
            return cached;
        }

        /// <summary>
        /// Get or extract — returns cached if available, otherwise extracts live
        /// using MEDIUM detail level. Result is stored for future calls.
        /// </summary>
        public CachedGeometry? GetOrExtract(Element el, Discipline disc)
        {
            if (el == null) return null;
            long uid = el.Id.Value;

            // Return cached (even if stale — stale entries are refreshed in background)
            if (_cache.TryGetValue(uid, out var cached)) return cached;

            // Live extraction with MEDIUM detail (safe for live monitor)
            try
            {
                var entry = BuildEntry(el, disc, useFineDetail: false);
                if (entry != null)
                    _cache[uid] = entry;
                return entry;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeoCache] Live extract error {uid}: {ex.Message}");
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  INVALIDATE  — mark elements as stale on DocumentChanged
        // ════════════════════════════════════════════════════════════════

        public void Invalidate(IEnumerable<ElementId> ids)
        {
            foreach (var id in ids)
                _stale[id.Value] = true;
        }

        public void InvalidateOne(ElementId id)
        {
            if (id != null) _stale[id.Value] = true;
        }

        // ════════════════════════════════════════════════════════════════
        //  REFRESH STALE  — background refresh of invalidated entries
        //  Call from Idling handler on background thread.
        //  Returns number of entries refreshed.
        // ════════════════════════════════════════════════════════════════

        public int RefreshStale(Document doc, int maxPerCycle = 50)
        {
            if (_stale.IsEmpty) return 0;

            var toRefresh = _stale.Keys.Take(maxPerCycle).ToList();
            int refreshed = 0;

            foreach (long uid in toRefresh)
            {
                try
                {
                    var el = doc.GetElement(new ElementId((int)uid));
                    if (el == null)
                    {
                        // Element deleted — remove from cache entirely
                        _cache.TryRemove(uid, out _);
                        _stale.TryRemove(uid, out _);
                        continue;
                    }

                    var disc = ElementCollector.GetDiscipline(el);
                    if (disc == Discipline.Unknown)
                    {
                        _cache.TryRemove(uid, out _);
                        _stale.TryRemove(uid, out _);
                        continue;
                    }

                    var entry = BuildEntry(el, disc, useFineDetail: false);
                    if (entry != null)
                        _cache[uid] = entry;
                    else
                        _cache.TryRemove(uid, out _);

                    _stale.TryRemove(uid, out _);
                    refreshed++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GeoCache] Refresh error {uid}: {ex.Message}");
                    _stale.TryRemove(uid, out _);
                }
            }

            return refreshed;
        }

        // ════════════════════════════════════════════════════════════════
        //  CLEAR
        // ════════════════════════════════════════════════════════════════

        public void Clear()
        {
            _cache.Clear();
            _stale.Clear();
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD ENTRY  — extract geometry from element
        // ════════════════════════════════════════════════════════════════

        private CachedGeometry? BuildEntry(Element el, Discipline disc, bool useFineDetail)
        {
            if (el == null || el.Category == null) return null;
            if (el.Location == null) return null;

            var bb = el.get_BoundingBox(null);
            if (bb == null) return null;

            var opts = useFineDetail ? _geomOptsFull : _geomOptsLive;
            Solid? bestSolid = null;

            try
            {
                var geom = el.get_Geometry(opts);
                if (geom != null)
                    bestSolid = ExtractBestSolid(geom);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeoCache] Geometry extract {el.Id.Value}: {ex.Message}");
            }

            // Must have a solid to be worth caching (unless structural)
            if (bestSolid == null && disc != Discipline.Structural) return null;

            double clearFt  = GetClearanceFt(disc);
            var    clearBox = Expand(bb, clearFt);

            // Get system type from parameters
            string sysType = "";
            try
            {
                var p = el.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)
                     ?? el.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                sysType = p?.AsString() ?? "";
            }
            catch { }

            // Get level
            string levelId = "";
            try
            {
                var p = el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                     ?? el.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
                if (p != null) levelId = p.AsElementId().ToString();
            }
            catch { }

            return new CachedGeometry
            {
                ElementId  = el.Id,
                Solid      = bestSolid,
                BoundingBox = bb,
                Center     = new XYZ(
                    (bb.Min.X + bb.Max.X) / 2,
                    (bb.Min.Y + bb.Max.Y) / 2,
                    (bb.Min.Z + bb.Max.Z) / 2),
                ClearanceBox = clearBox,
                Discipline   = disc,
                SystemType   = sysType,
                LevelId      = levelId,
                CachedAt     = DateTime.Now,
                IsValid      = true
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  GEOMETRY HELPERS
        // ════════════════════════════════════════════════════════════════

        private static Solid? ExtractBestSolid(GeometryElement geomEl)
        {
            Solid? best = null;
            double bestVol = MinSolidVolume;

            foreach (GeometryObject obj in geomEl)
            {
                if (obj is Solid s && s.Volume > bestVol)
                {
                    best    = s;
                    bestVol = s.Volume;
                }
                else if (obj is GeometryInstance gi)
                {
                    var nested = ExtractBestSolid(gi.GetInstanceGeometry());
                    if (nested != null && nested.Volume > bestVol)
                    {
                        best    = nested;
                        bestVol = nested.Volume;
                    }
                }
            }
            return best;
        }

        private static BoundingBoxXYZ Expand(BoundingBoxXYZ bb, double ft)
        {
            var e = new BoundingBoxXYZ();
            e.Min = new XYZ(bb.Min.X - ft, bb.Min.Y - ft, bb.Min.Z - ft);
            e.Max = new XYZ(bb.Max.X + ft, bb.Max.Y + ft, bb.Max.Z + ft);
            return e;
        }

        private static double GetClearanceFt(Discipline d)
        {
            switch (d)
            {
                case Discipline.HVAC:            return 0.164;  // 50 mm
                case Discipline.Plumbing:        return 0.098;  // 30 mm
                case Discipline.GravityDrainage: return 0.098;
                case Discipline.Electrical:      return 0.197;  // 60 mm
                case Discipline.FireProtection:  return 0.164;
                case Discipline.MedicalGas:      return 0.164;
                default:                         return 0.328;  // 100 mm
            }
        }
    }
}
