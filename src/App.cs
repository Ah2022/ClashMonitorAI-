// App.cs  — v5.1
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.LiveMonitor;
using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace ClashResolveAI
{
    public class App : IExternalApplication
    {
        public static bool        MonitorActive  { get; set; } = false;
        public static PushButton? MonitorButton  { get; private set; }

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                // ── 1. Register Clash Radar placement blocker (v5.1) ──────
                // ClashFailures.Register();
                // ClashPlacementUpdater.Register(app.ActiveAddInId); // Moved to LiveMonitorService.Start()

                const string tab = "MEP AI Tools";
                const string pan = "ClashResolve AI v5.1";
                try { app.CreateRibbonTab(tab); } catch { }
                var    rp  = app.CreateRibbonPanel(tab, pan);
                string dll = Assembly.GetExecutingAssembly().Location;

                // Full Scan
                rp.AddItem(Btn("DetectClashes", "Full\nScan", dll,
                    "ClashResolveAI.Commands.DetectClashesCommand",
                    "Full model scan: Spatial Hash Grid + Geometry Cache + Batch processing.\nDetects hard, soft, clearance and cross-link clashes with grouping.",
                    "detect_16.png", "detect_32.png"));
                rp.AddSeparator();

                // Generate RFIs (BCF + Word + Excel + AI)
                rp.AddItem(Btn("GenerateRFIs", "Generate\nRFIs", dll,
                    "ClashResolveAI.Commands.GenerateRFIsCommand",
                    "Export BCF 2.1 (Navisworks/Solibri), Word RFI report, Excel coordination sheet, AI suggestions.",
                    "rfi_16.png", "rfi_32.png"));
                rp.AddSeparator();

                // Live Monitor
                var monData = Btn("LiveMonitor", "Live\nMonitor", dll,
                    "ClashResolveAI.Commands.LiveMonitorCommand",
                    "▶ START lightweight real-time clash monitoring (bounding box + spatial grid, no Booleans).",
                    "monitor_16.png", "monitor_32.png");
                MonitorButton = rp.AddItem(monData) as PushButton;
                rp.AddSeparator();

                // Dashboard
                rp.AddItem(Btn("Dashboard", "Dashboard", dll,
                    "ClashResolveAI.Commands.DashboardCommand",
                    "Professional BIM Coordination Dashboard:\nGrouped issues, discipline matrix, trend analytics, lifecycle management.",
                    "dashboard_16.png", "dashboard_32.png"));
                rp.AddSeparator();

                // Settings
                rp.AddItem(Btn("Settings", "Settings", dll,
                    "ClashResolveAI.Commands.SettingsCommand",
                    "API key, rule sets (JSON), clearance values, linked model options, coordination zones.",
                    "settings_16.png", "settings_32.png"));

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ClashResolve AI v5.1 — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            // try { ClashPlacementUpdater.Unregister(app.ActiveAddInId); } catch { } // Moved to LiveMonitorService.Stop()
            LiveMonitor.LiveMonitorService.Instance?.Stop();
            ClashDatabase.Instance?.Dispose();
            return Result.Succeeded;
        }

        public static void RefreshMonitorButton()
        {
            if (MonitorButton == null) return;
            if (MonitorActive)
            {
                MonitorButton.ToolTip    = "⏹ Live Monitor ACTIVE — click to STOP.";
                MonitorButton.LargeImage = I("alert_32.png");
                MonitorButton.Image      = I("alert_16.png");
            }
            else
            {
                MonitorButton.ToolTip    = "▶ Click to START lightweight clash monitoring.";
                MonitorButton.LargeImage = I("monitor_32.png");
                MonitorButton.Image      = I("monitor_16.png");
            }
        }

        private static PushButtonData Btn(string name, string text, string dll,
            string cls, string tip, string img16, string img32) =>
            new PushButtonData(name, text, dll, cls)
            {
                ToolTip    = tip,
                Image      = I(img16),
                LargeImage = I(img32)
            };

        internal static BitmapImage? I(string fileName)
        {
            try
            {
                string asmName = Assembly.GetExecutingAssembly().GetName().Name!;
                var uri = new Uri(
                    $"pack://application:,,,/{asmName};component/Resources/{fileName}",
                    UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }

            try
            {
                string dir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string path = Path.Combine(dir, "Resources", fileName);
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
