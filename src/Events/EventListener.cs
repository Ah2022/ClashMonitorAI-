// Events/EventListener.cs  — v5.1
//
// FIXES v5.1:
//   BUG 1 — _internalUpdate deadlock:
//            The flag is now time-bounded (auto-resets after 3s) so a failed
//            or dropped ExternalEvent can never permanently block the selection path.
//   BUG 2 — _internalUpdate applied to SelectionChanged incorrectly:
//            Removed from ProcessCurrentSelection. It only guards DocumentChanged
//            and Idling (where a highlight loop is actually possible).
//   BUG 3 — Linked structural elements not found:
//            ProcessCurrentSelection now calls RunSelectionScanWithLinks (new in
//            ClashEngine v5.1) which also scans linked documents for structural.
//   BUG 4 — (Fixed in ElementCollector.cs) — more categories recognised.

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ClashResolveAI.Alert;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard;
using ClashResolveAI.LiveMonitor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ClashEngineNS = ClashResolveAI.ClashEngine.ClashEngine;

namespace ClashResolveAI.Events
{
    public class EventListener
    {
        private readonly UIApplication  _app;
        private readonly AlertSystem    _alert;
        private readonly ClashTaskQueue _queue = ClashTaskQueue.Instance;

        // ── DocumentChanged debounce ──────────────────────────────────
        private readonly HashSet<long> _pending     = new HashSet<long>();
        private readonly object        _pendingLock = new object();
        private DateTime _lastChange = DateTime.MinValue;
        private bool     _hasPending = false;
        private const int DebounceMs = 1500;

        // ── Duplicate suppression ─────────────────────────────────────
        private readonly HashSet<string> _reported = new HashSet<string>();

        // ── Internal update guard (prevents DocumentChanged loop) ─────
        // FIX: time-bounded so a dropped ExternalEvent can't deadlock
        private volatile bool _internalUpdate = false;
        private DateTime      _internalUpdateTime = DateTime.MinValue;
        private const int     InternalUpdateGuardMs = 3000; // 3-second auto-reset

        // ── Selection tracking ────────────────────────────────────────
        private long _lastSelectedId = -1;

        public bool IsActive { get; private set; }

        // ─────────────────────────────────────────────────────────────
        public bool InternalUpdate
        {
            get => _internalUpdate;
            set
            {
                _internalUpdate     = value;
                _internalUpdateTime = value ? DateTime.Now : DateTime.MinValue;
            }
        }

        public EventListener(UIApplication app, AlertSystem alert)
        {
            _app   = app;
            _alert = alert;
        }

        // ════════════════════════════════════════════════════════════════
        //  START / STOP
        // ════════════════════════════════════════════════════════════════

        public void Start()
        {
            if (IsActive) return;

            _reported.Clear();
            _queue.Clear();
            lock (_pendingLock) { _pending.Clear(); _hasPending = false; }
            _lastChange         = DateTime.MinValue;
            _internalUpdate     = false;
            _internalUpdateTime = DateTime.MinValue;
            _lastSelectedId     = -1;

            _app.Application.DocumentChanged += OnDocumentChanged;
            _app.Idling                       += OnIdling;
            _app.SelectionChanged             += OnSelectionChanged;

            IsActive = true;
            Debug.WriteLine("[EventListener] v5.1 started — Clash Radar mode.");
        }

        public void Stop()
        {
            if (!IsActive) return;

            _app.Application.DocumentChanged -= OnDocumentChanged;
            _app.Idling                       -= OnIdling;
            _app.SelectionChanged             -= OnSelectionChanged;

            _queue.Clear();
            lock (_pendingLock) { _pending.Clear(); _hasPending = false; }
            _internalUpdate     = false;
            _internalUpdateTime = DateTime.MinValue;
            _lastSelectedId     = -1;
            IsActive            = false;

            Debug.WriteLine("[EventListener] Stopped.");
        }

        public void ClearReported()
        {
            _reported.Clear();
            Debug.WriteLine("[EventListener] Reported set cleared.");
        }

        // FIX BUG 1: OnHighlightComplete resets both the flag AND the timestamp
        public void OnHighlightComplete()
        {
            _internalUpdate     = false;
            _internalUpdateTime = DateTime.MinValue;
            Debug.WriteLine("[EventListener] InternalUpdate cleared.");
        }

        public bool CheckCurrentSelection() => ProcessCurrentSelection(force: true);

        // ════════════════════════════════════════════════════════════════
        //  A) SelectionChanged — instant lightweight check
        //
        //  FIX BUG 2: _internalUpdate is NOT checked here.
        //  Selections never commit a transaction so there is no loop risk.
        //  The only loop risk is DocumentChanged → Idling → highlight → 
        //  DocumentChanged, which is guarded in those paths separately.
        // ════════════════════════════════════════════════════════════════

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ProcessCurrentSelection(force: false);
        }

        private bool ProcessCurrentSelection(bool force)
        {
            try
            {
                // FIX BUG 2: No _internalUpdate check here.

                var uidoc = _app.ActiveUIDocument;
                var doc   = uidoc?.Document;
                if (doc == null || doc.IsReadOnly || doc.IsFamilyDocument) return false;

                var selIds = uidoc!.Selection.GetElementIds();
                if (selIds == null || selIds.Count == 0)
                {
                    if (_lastSelectedId != -1)
                    {
                        _lastSelectedId = -1;
                        _alert.ShowSelectionClearToast();
                    }
                    return false;
                }

                // Find first selectable element with a known discipline
                // (structural OR any MEP — both can be the source of a scan)
                Element? selected = null;
                foreach (var id in selIds)
                {
                    try
                    {
                        var el = doc.GetElement(id);
                        if (el?.Category == null) continue;

                        // Accept any discipline that has clash-matrix entries
                        var disc = ElementCollector.GetDiscipline(el);
                        if (disc == Discipline.Unknown) continue;

                        // Must have a bounding box to be usable
                        if (el.get_BoundingBox(null) == null) continue;

                        selected = el;
                        break;
                    }
                    catch { }
                }

                if (selected == null) { _lastSelectedId = -1; return false; }
                if (!force && _lastSelectedId == selected.Id.Value) return false;
                _lastSelectedId = selected.Id.Value;

                Debug.WriteLine($"[EventListener] Selection: ID {selected.Id.Value} " +
                    $"({selected.Category?.Name}) disc={ElementCollector.GetDiscipline(selected)}");

                // FIX BUG 3: use RunSelectionScanWithLinks — also checks linked models
                var engine  = new ClashEngineNS(doc);
                var clashes = engine.RunSelectionScanWithLinks(selected);

                if (clashes.Any())
                {
                    Debug.WriteLine($"[EventListener] Selection: {clashes.Count} clash(es) found.");
                    RadarDataStore.Instance.AddClashes(clashes);
                    ClashDashboard.Instance.AddClashes(clashes);
                    _alert.TriggerSelectionAlerts(_app, selected, clashes);
                }
                else
                {
                    Debug.WriteLine("[EventListener] Selection: no clashes.");
                    _alert.ShowSelectionClearToast(selected);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EventListener] SelectionChanged error: {ex.Message}");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  B) DocumentChanged — read-only queue, no geometry
        //
        //  FIX BUG 1: _internalUpdate is time-bounded; auto-resets here too.
        // ════════════════════════════════════════════════════════════════

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                // Auto-reset stale _internalUpdate guard (BUG 1 fix)
                if (_internalUpdate &&
                    (DateTime.Now - _internalUpdateTime).TotalMilliseconds > InternalUpdateGuardMs)
                {
                    Debug.WriteLine("[EventListener] _internalUpdate auto-reset (timeout).");
                    _internalUpdate     = false;
                    _internalUpdateTime = DateTime.MinValue;
                }

                // Block if we caused this change ourselves (highlight transaction)
                if (_internalUpdate) return;

                var doc = e.GetDocument();
                if (doc == null || doc.IsReadOnly || doc.IsFamilyDocument) return;

                var added    = e.GetAddedElementIds();
                var modified = e.GetModifiedElementIds();
                if (!added.Any() && !modified.Any()) return;

                lock (_pendingLock)
                {
                    foreach (var id in added)    _pending.Add(id.Value);
                    foreach (var id in modified) _pending.Add(id.Value);
                    _hasPending = true;
                }
                _lastChange = DateTime.Now;

                Debug.WriteLine($"[EventListener] DocumentChanged: " +
                    $"+{added.Count} added, ~{modified.Count} modified.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EventListener] DocumentChanged error: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  C) Idling — geometry ops after debounce
        //
        //  FIX BUG 3: calls RunTargetedScanWithLinks (also checks linked models)
        // ════════════════════════════════════════════════════════════════

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                if (!_hasPending) return;
                if ((DateTime.Now - _lastChange).TotalMilliseconds < DebounceMs) return;

                List<long> toProcess;
                lock (_pendingLock)
                {
                    if (_pending.Count == 0) { _hasPending = false; return; }
                    toProcess = new List<long>(_pending);
                    _pending.Clear();
                    _hasPending = false;
                }

                var uidoc = _app.ActiveUIDocument;
                var doc   = uidoc?.Document;
                if (doc == null || doc.IsReadOnly || doc.IsFamilyDocument) return;

                var elementIds = toProcess.Select(id => new ElementId(id)).ToList();
                GeometryCacheService.Instance.Invalidate(elementIds);

                Debug.WriteLine($"[EventListener] Idling scan: {toProcess.Count} changed element(s).");

                // FIX BUG 3: RunTargetedScanWithLinks also checks linked models
                var engine  = new ClashEngineNS(doc);
                var clashes = engine.RunTargetedScanWithLinks(elementIds);

                if (!clashes.Any())
                {
                    Debug.WriteLine("[EventListener] Idling: no clashes.");
                    _alert.ShowNoClashToast();
                    return;
                }

                var fresh = clashes
                    .Where(c => _reported.Add(NormalizedKey(c)))
                    .ToList();

                if (fresh.Any())
                {
                    Debug.WriteLine($"[EventListener] Idling: {fresh.Count} new clash(es).");
                    RadarDataStore.Instance.AddClashes(fresh);
                    ClashDashboard.Instance.AddClashes(fresh);
                }

                // Set guard BEFORE raising event (BUG 1 fix: include timestamp)
                InternalUpdate = true;
                try
                {
                    // v5.1: Always show alerts for the current clashes of the modified elements
                    _alert.TriggerAlerts(_app, clashes);
                }
                catch (Exception ex)
                {
                    // Raise failed — reset immediately so guard doesn't stick
                    InternalUpdate = false;
                    Debug.WriteLine($"[EventListener] TriggerAlerts error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                InternalUpdate = false;
                Debug.WriteLine($"[EventListener] Idling error: {ex.Message}");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────

        private static string NormalizedKey(ClashResult c)
        {
            long a = Math.Min(c.ElementA?.Id.Value ?? 0, c.ElementB?.Id.Value ?? 0);
            long b = Math.Max(c.ElementA?.Id.Value ?? 0, c.ElementB?.Id.Value ?? 0);
            return $"{a}:{b}";
        }
    }
}
