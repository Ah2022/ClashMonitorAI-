// Alert/AlertSystem.cs  — v4.0
// Non-blocking toast overlay + ExternalEvent-based highlight handler.
// Highlights reset only previously highlighted IDs, not full view scan.
// _internalUpdate cleared AFTER transaction commit only.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using ClashResolveAI.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace ClashResolveAI.Alert
{
    public class AlertSystem
    {
        private static readonly Color ColCritical  = new Color(220,  50,  50);
        private static readonly Color ColHard      = new Color(230, 120,   0);
        private static readonly Color ColSoft      = new Color(230, 200,   0);
        private static readonly Color ColClearance = new Color( 80, 160, 220);

        private readonly HashSet<ElementId>    _highlightedIds  = new HashSet<ElementId>();
        private readonly HighlightEventHandler _highlightHandler = new HighlightEventHandler();
        private ExternalEvent? _highlightEvent;

        private void EnsureEvent(UIApplication app)
        {
            if (_highlightEvent == null)
            {
                _highlightHandler.HighlightedIds = _highlightedIds;
                _highlightEvent = ExternalEvent.Create(_highlightHandler);
            }
        }

        public void TriggerAlerts(UIApplication app, List<ClashResult> clashes)
        {
            if (clashes == null || !clashes.Any()) return;
            EnsureEvent(app);
            _highlightHandler.Clashes = clashes;
            _highlightEvent!.Raise();
            ShowToast(clashes);
        }

        public void TriggerSelectionAlerts(UIApplication app, Element selected, List<ClashResult> clashes)
        {
            if (clashes == null || !clashes.Any()) return;
            
            int count = clashes.Count;
            string title = $"⚠️ {count} Clashes Detected";
            string cat   = selected.Category?.Name ?? "Element";
            string body  = $"Selected: {cat} (ID:{selected.Id.Value})";
            
            int crit = clashes.Count(c => c.Severity == ClashSeverity.Critical);
            int hard = clashes.Count(c => c.Severity == ClashSeverity.Hard);
            string hex = crit > 0 ? "#DC3232" : hard > 0 ? "#E67800" : "#E6C800";
            
            new ToastWindow(title, body, hex).ShowForSeconds(5);
        }

        public void ShowSelectionClearToast(Element? el = null)
        {
            string msg = el == null ? "✅ No selection."
                : $"✅ {el.Category?.Name ?? "Element"} (ID:{el.Id.Value}) — No clashes.";
            new ToastWindow(msg, "", "#27AE60").ShowForSeconds(3);
        }

        public void ShowNoClashToast()
        {
            new ToastWindow("✅ No Clash", "Real-time coordination active.", "#27AE60").ShowForSeconds(3);
        }

        public void ShowMonitorStatusToast(bool running)
        {
            // v5.0 — renamed "Live Monitor" → "Clash Radar" in all user-facing text
            string title = running
                ? "Clash Radar — Monitoring Active"
                : "Clash Radar — Monitoring Stopped";
            string body = running
                ? "Panel is open on the right. Clashes are detected as you model and select elements."
                : "Real-time clash detection is off. The radar panel shows your last session.";
            string hex = running ? "#2980B9" : "#7F8C8D";
            new ToastWindow(title, body, hex).ShowForSeconds(3);
        }

        private static string CleanSystem(string value) =>
            string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

        private static void ShowToast(List<ClashResult> clashes)
        {
            try
            {
                int count = clashes.Count;
                string title = $"⚠️ {count} Clashes Detected";
                string body  = "Real-time coordination active.";
                
                int crit = clashes.Count(c => c.Severity == ClashSeverity.Critical);
                int hard = clashes.Count(c => c.Severity == ClashSeverity.Hard);
                
                string hex = crit > 0 ? "#DC3232" : hard > 0 ? "#E67800" : "#E6C800";
                new ToastWindow(title, body, hex).ShowForSeconds(5);
            }
            catch (Exception ex) { Debug.WriteLine($"[Alert] Toast: {ex.Message}"); }
        }
    }

    // ── Highlight handler (UI thread via ExternalEvent) ──────────────────
    internal class HighlightEventHandler : IExternalEventHandler
    {
        public List<ClashResult>?  Clashes        { get; set; }
        public HashSet<ElementId>  HighlightedIds { get; set; } = new HashSet<ElementId>();

        private static readonly Color ColCritical  = new Color(220,  50,  50);
        private static readonly Color ColHard      = new Color(230, 120,   0);
        private static readonly Color ColSoft      = new Color(230, 200,   0);
        private static readonly Color ColClearance = new Color( 80, 160, 220);

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || Clashes == null) return;
            try
            {
                using var tx = new Transaction(doc, "ClashResolve — Highlight");
                tx.Start();
                var view = doc.ActiveView;

                // Reset only previously highlighted IDs
                var clear = new OverrideGraphicSettings();
                foreach (var id in HighlightedIds)
                {
                    try { var e = doc.GetElement(id); if (e != null) view.SetElementOverrides(id, clear); }
                    catch (Exception ex) { Debug.WriteLine($"[Highlight] Reset {id.Value}: {ex.Message}"); }
                }
                HighlightedIds.Clear();

                // Apply new highlights
                foreach (var clash in Clashes.Take(200))
                {
                    Color col = clash.Severity switch {
                        ClashSeverity.Critical  => ColCritical,
                        ClashSeverity.Hard      => ColHard,
                        ClashSeverity.Soft      => ColSoft,
                        ClashSeverity.Clearance => ColClearance,
                        _                       => ColHard
                    };
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(col);
                    ogs.SetSurfaceForegroundPatternColor(col);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                    ogs.SetProjectionLineWeight(4);
                    try
                    {
                        if (clash.ElementA?.IsValidObject == true) { view.SetElementOverrides(clash.ElementA.Id, ogs); HighlightedIds.Add(clash.ElementA.Id); }
                        if (clash.ElementB?.IsValidObject == true) { view.SetElementOverrides(clash.ElementB.Id, ogs); HighlightedIds.Add(clash.ElementB.Id); }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[Highlight] Override: {ex.Message}"); }
                }
                tx.Commit();
            }
            catch (Exception ex) { Debug.WriteLine($"[Highlight] Execute: {ex.Message}"); }
            finally { LiveMonitor.LiveMonitorService.Instance?.ResetInternal(); }
        }
        public string GetName() => "ClashResolveAI_HighlightHandler";
    }

    // ── Non-blocking toast overlay ────────────────────────────────────────
    internal class ToastWindow : Window
    {
        private System.Windows.Threading.DispatcherTimer? _timer;

        public ToastWindow(string title, string body, string hex)
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true;
            Background  = System.Windows.Media.Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight; MaxWidth = 500;
            
            // Position: Top Center
            var wa = System.Windows.SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            
            var col = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(hex);

            var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            
            // Icon / Badge
            var iconBdr = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(col),
                CornerRadius = new CornerRadius(12),
                Width = 24, Height = 24, Margin = new Thickness(0,0,10,0)
            };
            iconBdr.Child = new System.Windows.Controls.TextBlock {
                Text = "⚠️", FontSize = 12, Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            sp.Children.Add(iconBdr);

            var textStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new System.Windows.Controls.TextBlock {
                Text = title, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = System.Windows.TextWrapping.Wrap });
            
            if (!string.IsNullOrEmpty(body))
                textStack.Children.Add(new System.Windows.Controls.TextBlock {
                    Text = body, FontSize = 10,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(200, 210, 220)),
                    TextWrapping = System.Windows.TextWrapping.Wrap });
            
            sp.Children.Add(textStack);

            Content = new System.Windows.Controls.Border {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(220, 30, 35, 45)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(col),
                BorderThickness = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(20, 10, 25, 10), 
                Child = sp 
            };
            
            // Center horizontally at the top
            Loaded += (s, e) => {
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = wa.Top + 60;
            };
        }

        public void ShowForSeconds(double secs)
        {
            Application.Current?.Dispatcher?.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(() => {
                    Show();
                    _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                    _timer.Tick += (s, e) => { _timer.Stop(); Close(); };
                    _timer.Start();
                }));
        }
    }
}
