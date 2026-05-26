// LiveMonitor/LiveMonitorService.cs  — v5.0
//
// UPGRADE: Live Monitor → Clash Radar
//
// v4.0 behaviour:
//   SelectionChanged → lightweight AABB scan → toast notification only
//
// v5.0 behaviour (Clash Radar):
//   1. On activation  : show toast with initial clash count
//                       open ClashRadarPanel (floating right-side panel)
//   2. Continuous     : EventListener feeds RadarDataStore in real-time
//                       RadarDataStore.DataChanged drives panel updates
//   3. Panel features : columns #/Time/CategoryA/CategoryB/IDB/Location
//                       category filter, Show3D/Show2D/Ignore/Refresh/Export
//                       mini clash preview canvas
//   4. Performance    : ALL geometry work stays on background/Idling thread
//                       WPF panel polls via DataChanged event — zero Revit
//                       API calls on the WPF dispatcher thread

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Alert;
using ClashResolveAI.Events;
using System;
using System.Diagnostics;
using System.Linq;

using ClashEngineNS = ClashResolveAI.ClashEngine.ClashEngine;

namespace ClashResolveAI.LiveMonitor
{
    public class LiveMonitorService
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static LiveMonitorService? _instance;
        public  static LiveMonitorService   Instance =>
            _instance ?? (_instance = new LiveMonitorService());

        // ── Components ─────────────────────────────────────────────────
        private EventListener?     _listener;
        private AlertSystem?       _alert;
        private ExternalEvent?     _navEvent;
        private ExternalEvent?     _refreshEvent;
        private AddInId?           _addInId;

        public bool IsRunning { get; private set; }

        // ══════════════════════════════════════════════════════════════
        //  START — activate Clash Radar
        // ══════════════════════════════════════════════════════════════

        public void Start(UIApplication app)
        {
            if (IsRunning) return;
            _addInId = app.ActiveAddInId;

            // ── 1. Register Placement Blocker (v5.1 update) ───────────
            // try { ClashPlacementUpdater.Register(_addInId); } catch { }

            // ── 2. Initialise components ───────────────────────────────
            _alert    = new AlertSystem();
            _listener = new EventListener(app, _alert);
            _listener.Start();

            // ── 2. Wire ExternalEvents for WPF panel navigation ────────
            var navHandler     = new ClashNavHandler();
            var refreshHandler = new ClashRefreshHandler();
            _navEvent          = ExternalEvent.Create(navHandler);
            _refreshEvent      = ExternalEvent.Create(refreshHandler);

            // ── 3. Create & configure radar panel (on UI/STA thread) ───
            var panel = ClashRadarPanel.Instance;
            panel.SetRevitEvents(_navEvent, _refreshEvent);
            panel.SetActiveState(true);
            panel.Show();

            IsRunning = true;
            Debug.WriteLine("[ClashRadar] Started — Radar panel open.");

            // ── 4. Initial scan: populate panel immediately ────────────
            RunInitialScan(app);
        }

        // ══════════════════════════════════════════════════════════════
        //  STOP — deactivate Clash Radar
        // ══════════════════════════════════════════════════════════════

        public void Stop()
        {
            if (!IsRunning) return;

            // ── 0. Unregister Placement Blocker ───────────────────────
            if (_addInId != null)
            {
                try { ClashPlacementUpdater.Unregister(_addInId); } catch { }
            }

            _listener?.Stop();
            _listener = null;
            _alert    = null;

            // Put panel in idle state but keep it visible (user may want to
            // review the list). User can close with the ✕ button.
            if (ClashRadarPanel.IsVisible)
                ClashRadarPanel.Instance.SetActiveState(false);

            // Show stop toast
            new ToastWindow("Clash Radar stopped",
                $"Last session: {RadarDataStore.Instance.TotalDetected} clash(es) detected.",
                "#7F8C8D").ShowForSeconds(3);

            IsRunning = false;
            Debug.WriteLine("[ClashRadar] Stopped.");
        }

        // ══════════════════════════════════════════════════════════════
        //  INITIAL SCAN  — runs on Revit's idle / selection path
        // ══════════════════════════════════════════════════════════════

        private void RunInitialScan(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc   = uidoc?.Document;
                if (doc == null || doc.IsReadOnly) return;

                // Clear previous session data
                RadarDataStore.Instance.Clear();

                // ── v5.1: Perform targeted light scan on startup for selected elements only ──────────
                // This satisfies: "i dont want a project full light scan i want a scan for only the element i modify it or drawing it"
                var engine  = new ClashEngineNS(doc);
                var selIds  = uidoc.Selection.GetElementIds();
                
                if (selIds != null && selIds.Any())
                {
                    foreach (var sid in selIds)
                    {
                        var el = doc.GetElement(sid);
                        if (el == null) continue;
                        var clashes = engine.RunSelectionScanWithLinks(el);
                        if (clashes.Any())
                            RadarDataStore.Instance.AddClashes(clashes);
                    }
                }

                // Toast: tell user how many clashes were found at startup
                int count = RadarDataStore.Instance.ActiveCount;
                if (count > 0)
                {
                    new ToastWindow(
                        $"⚠️ {count} Clashes Detected",
                        "Clash Radar is active. Monitor panel opened on the right.",
                        "#E67E22").ShowForSeconds(5);
                }
                else
                {
                    new ToastWindow(
                        "✅ Clash Radar — No Clashes",
                        "Monitoring is active. Clashes will appear as you model.",
                        "#2980B9").ShowForSeconds(4);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClashRadar] InitialScan error: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  DELEGATED to EventListener (existing callers unchanged)
        // ══════════════════════════════════════════════════════════════

        public void ClearHistory()          => _listener?.ClearReported();
        public void ResetInternal()         => _listener?.OnHighlightComplete();
        public bool CheckCurrentSelection() => _listener?.CheckCurrentSelection() == true;
        public void ShowStatusToast(bool running) => _alert?.ShowMonitorStatusToast(running);
    }
}
