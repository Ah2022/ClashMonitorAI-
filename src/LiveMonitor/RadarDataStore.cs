// LiveMonitor/RadarDataStore.cs  — v5.0
//
// Thread-safe singleton data store.
// Writer : EventListener  (Revit threads)
// Reader : ClashRadarPanel (WPF dispatcher thread, polled every 400ms)
//
// Zero Revit API calls — pure data, safe to read from any thread.

using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.LiveMonitor
{
    public class RadarDataStore
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static RadarDataStore? _instance;
        public static RadarDataStore Instance =>
            _instance ?? (_instance = new RadarDataStore());

        // ── State ──────────────────────────────────────────────────────
        private readonly object            _lock          = new object();
        private readonly List<ClashResult> _active        = new List<ClashResult>();
        private readonly HashSet<string>   _ignoredKeys   = new HashSet<string>();
        private int                        _totalDetected = 0;

        // Raised on the thread that called AddClashes / IgnoreClash / Clear.
        // ClashRadarPanel subscribes and dispatches to UI thread internally.
        public event EventHandler? DataChanged;

        private RadarDataStore() { }

        // ── Write API ──────────────────────────────────────────────────

        public void AddClashes(IEnumerable<ClashResult> incoming)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var c in incoming)
                {
                    string key = Key(c);
                    if (_ignoredKeys.Contains(key)) continue;
                    if (_active.Any(x => Key(x) == key)) continue;
                    _active.Add(c);
                    _totalDetected++;
                    changed = true;
                }
            }
            if (changed) DataChanged?.Invoke(this, EventArgs.Empty);
        }

        public void IgnoreClash(ClashResult clash)
        {
            lock (_lock)
            {
                string key = Key(clash);
                _ignoredKeys.Add(key);
                _active.RemoveAll(c => Key(c) == key);
            }
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Re-validates all stored clashes and removes any whose
        /// bounding boxes no longer overlap (called by Refresh handler).
        /// </summary>
        public void PruneResolved(Func<ClashResult, bool> stillClashing)
        {
            bool changed = false;
            lock (_lock)
            {
                int before = _active.Count;
                _active.RemoveAll(c => !stillClashing(c));
                if (_active.Count != before) changed = true;
            }
            if (changed) DataChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            lock (_lock)
            {
                _active.Clear();
                _ignoredKeys.Clear();
                _totalDetected = 0;
            }
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Read API ───────────────────────────────────────────────────

        public List<ClashResult> GetActive()
        {
            lock (_lock) return new List<ClashResult>(_active);
        }

        public int TotalDetected
        {
            get { lock (_lock) return _totalDetected; }
        }

        public int ActiveCount
        {
            get { lock (_lock) return _active.Count; }
        }

        /// <summary>Returns sorted, unique category names across all active clashes.</summary>
        public List<string> GetCategories()
        {
            lock (_lock)
            {
                var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in _active)
                {
                    var nameA = c.ElementA?.Category?.Name ?? c.DisciplineA.ToString();
                    var nameB = c.ElementB?.Category?.Name ?? c.DisciplineB.ToString();
                    if (!string.IsNullOrWhiteSpace(nameA)) cats.Add(nameA);
                    if (!string.IsNullOrWhiteSpace(nameB)) cats.Add(nameB);
                }
                return cats.OrderBy(x => x).ToList();
            }
        }

        // ── Helpers ────────────────────────────────────────────────────

        private static string Key(ClashResult c)
        {
            long a = Math.Min(c.ElementA?.Id.Value ?? 0, c.ElementB?.Id.Value ?? 0);
            long b = Math.Max(c.ElementA?.Id.Value ?? 0, c.ElementB?.Id.Value ?? 0);
            return $"{a}:{b}";
        }
    }
}
