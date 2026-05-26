// Commands/Commands.cs  — v5.0
//
// v5.0 — Clash Radar upgrade (LiveMonitorCommand only):
//   LiveMonitorCommand now delegates fully to LiveMonitorService v5,
//   which opens ClashRadarPanel on start and emits its own toasts.
//   All other commands (FullScan, ResolveClash, etc.) are UNCHANGED.

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClashResolveAI.AutoResolve;
using ClashResolveAI.Core;
using ClashResolveAI.Dashboard;
using ClashResolveAI.Engine;
using ClashResolveAI.Links;
using ClashResolveAI.LiveMonitor;
using ClashResolveAI.Rules;
using ClashResolveAI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using ClashEngineNS = ClashResolveAI.ClashEngine.ClashEngine;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace ClashResolveAI.Commands
{
    internal static class Session
    {
        public static List<ClashResult>? Clashes     { get; set; }
        public static List<ClashGroup>?  Groups      { get; set; }
        public static string             ProjectName { get; set; } = "Project";
        public static CoordinationZone?  ActiveZone  { get; set; }
        public static string             RuleSetName { get; set; } = "DefaultRules";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  1. FULL SCAN — with floor selection dialog
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DetectClashesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            var doc = data.Application.ActiveUIDocument.Document;
            try
            {
                var s = AppSettings.Load();
                Session.ProjectName = Path.GetFileNameWithoutExtension(doc.PathName) ?? "Project";
                ClashDatabase.Instance.Open(Session.ProjectName);

                // ── STEP 1: Floor selection ────────────────────────────────
                var picker = new LevelPickerDialog(doc);
                picker.ShowDialog();
                if (!picker.Confirmed) return Result.Cancelled;

                string selectedLevelId   = picker.SelectedLevelId;    // "" = all floors
                string selectedLevelName = picker.SelectedLevelName;   // "All Floors" or level name

                // ── STEP 2: Pre-scan info dialog ───────────────────────────
                var linkMgr       = new LinkedModelManager(doc);
                string linkInfo   = linkMgr.GetLinksSummary();
                var    ruleSets   = RulesEngine.GetAvailableRuleSets();
                string scopeLabel = string.IsNullOrEmpty(selectedLevelId)
                    ? "All Floors (full model)"
                    : $"Floor: {selectedLevelName} only";

                var td = new TaskDialog("ClashResolve AI v4.1 — Full Scan")
                {
                    MainInstruction = "Professional BIM Coordination Scan",
                    MainContent     =
                        $"Scope: {scopeLabel}\n" +
                        $"Rule Set: {Session.RuleSetName}\n" +
                        $"Linked models: {(s.ScanLinkedModels ? "YES" : "NO")}\n\n" +
                        $"{linkInfo}\n\n" +
                        "Click OK to start.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
                };
                if (td.Show() == TaskDialogResult.Cancel) return Result.Cancelled;

                // ── STEP 3: Run scan ───────────────────────────────────────
                var progress = new ProgressDialog($"Scanning {scopeLabel}…");
                progress.Show();

                var progressReporter = new Progress<string>(m =>
                {
                    progress.SetMessage(m);
                    System.Windows.Application.Current?.Dispatcher?.Invoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => { }));
                });

                var engine  = new ClashEngineNS(doc, Session.RuleSetName);
                var clashes = engine.RunFullScan(
                    includeLinks: s.ScanLinkedModels,
                    progress:     progressReporter,
                    zone:         Session.ActiveZone,
                    levelId:      selectedLevelId);   // LEVEL FILTER PASSED HERE

                progress.SetMessage("Proposing routing alternatives…");
                foreach (var c in clashes)
                    c.Alternatives = AutoResolveEngine.Propose(c, doc);

                progress.SetMessage("Grouping clashes (Navisworks-style)…");
                var grouper = new ClashGroupingEngine();
                var groups  = grouper.GroupClashes(clashes);
                string coordReport = ClashGroupingEngine.GenerateCoordinationReport(groups);

                progress.SetMessage("Persisting to database…");
                ClashDatabase.Instance.BulkInsertClashes(clashes);
                ClashDatabase.Instance.SaveWeeklySnapshot();
                foreach (var g in groups) ClashDatabase.Instance.UpsertGroup(g);

                Session.Clashes = clashes;
                Session.Groups  = groups;
                ClashDashboard.Instance.AddFullScanResults(clashes);
                progress.Close();

                // ── STEP 4: Results ────────────────────────────────────────
                int crit  = clashes.Count(c => c.Severity == ClashSeverity.Critical);
                int hard  = clashes.Count(c => c.Severity == ClashSeverity.Hard);
                int soft  = clashes.Count(c => c.Severity == ClashSeverity.Soft);
                int clr   = clashes.Count(c => c.Severity == ClashSeverity.Clearance);
                int xLink = clashes.Count(c =>
                    !string.IsNullOrEmpty(c.LinkFileA) || !string.IsNullOrEmpty(c.LinkFileB));
                double health = ClashDatabase.Instance.GetHealthScore();

                TaskDialog.Show("Scan Complete",
                    $"Scope: {scopeLabel}\n\n" +
                    $"Total Clashes : {clashes.Count}  |  Groups: {groups.Count}\n\n" +
                    $"  Critical   : {crit}\n" +
                    $"  Hard       : {hard}\n" +
                    $"  Soft       : {soft}\n" +
                    $"  Clearance  : {clr}\n" +
                    $"  Cross-link : {xLink}\n\n" +
                    $"  Health Score: {health:F0}%\n\n" +
                    $"Root Cause:\n" +
                    $"{string.Join("\n", coordReport.Split('\n').Take(8))}\n\n" +
                    "Open Dashboard to manage clash lifecycle.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                msg = ex.Message;
                return Result.Failed;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  2. GENERATE RFIs
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateRFIsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try
            {
                if (Session.Clashes == null || !Session.Clashes.Any())
                {
                    TaskDialog.Show("ClashResolve AI", "Run Full Scan first.");
                    return Result.Cancelled;
                }

                var s  = AppSettings.Load();
                var td = new TaskDialog("Generate RFIs & Reports")
                {
                    MainInstruction = "Select outputs to generate:",
                    MainContent =
                        "BCF 2.1 — Navisworks / Solibri compatible\n" +
                        "Excel coordination report\n" +
                        "Word RFI document\n" +
                        $"AI suggestions: {(string.IsNullOrEmpty(s.OpenAiApiKey) ? "No API key" : "Ready")}\n\n" +
                        $"Clashes: {Session.Clashes.Count}  Groups: {Session.Groups?.Count ?? 0}",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
                };
                if (td.Show() == TaskDialogResult.Cancel) return Result.Cancelled;

                var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select output folder" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
                string outFolder = dlg.SelectedPath;

                var generated = new List<string>();

                // BCF
                try
                {
                    string bcfPath = new BcfExportService().ExportClashes(
                        Session.Clashes, Session.ProjectName, outFolder);
                    generated.Add($"BCF: {Path.GetFileName(bcfPath)}");
                }
                catch (Exception ex) { generated.Add($"BCF failed: {ex.Message}"); }

                // AI
                if (!string.IsNullOrEmpty(s.OpenAiApiKey))
                {
                    try
                    {
                        var aiSvc     = new OpenAI.OpenAIService(s.OpenAiApiKey);
                        var topClashes = Session.Clashes
                            .Where(c => c.Severity <= ClashSeverity.Hard).Take(20).ToList();
                        Task.Run(async () =>
                        {
                            foreach (var c in topClashes)
                            {
                                var (suggestion, rfi) = await aiSvc.AnalyseAsync(c);
                                c.AiSuggestion = suggestion;
                                c.RfiText      = rfi;
                            }
                        }).Wait(TimeSpan.FromSeconds(30));
                        generated.Add($"AI: {topClashes.Count} suggestions");
                    }
                    catch (Exception ex) { generated.Add($"AI failed: {ex.Message}"); }
                }

                // Excel
                try
                {
                    string xlPath = Reports.ExcelReportGenerator.Generate(
                        Session.Clashes, Session.ProjectName, outFolder);
                    generated.Add($"Excel: {Path.GetFileName(xlPath)}");
                }
                catch (Exception ex) { generated.Add($"Excel failed: {ex.Message}"); }

                // Word
                try
                {
                    string docPath = Reports.WordReportGenerator.Generate(
                        Session.Clashes, Session.ProjectName, outFolder);
                    generated.Add($"Word: {Path.GetFileName(docPath)}");
                }
                catch (Exception ex) { generated.Add($"Word failed: {ex.Message}"); }

                TaskDialog.Show("Reports Generated",
                    $"Folder:\n{outFolder}\n\n" +
                    string.Join("\n", generated.Select(f => $"+ {f}")));

                System.Diagnostics.Process.Start(outFolder);
                return Result.Succeeded;
            }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  3. LIVE MONITOR
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class LiveMonitorCommand : IExternalCommand
    {
        // ── v5.0: Clash Radar ─────────────────────────────────────────────
        //
        //  ON  → Start() opens ClashRadarPanel + fires initial scan toast
        //  OFF → Stop() marks panel as idle; user keeps the list visible
        //
        //  No manual toast call needed here — LiveMonitorService.Start()
        //  runs RunInitialScan() which emits the appropriate toast.
        // ─────────────────────────────────────────────────────────────────
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try
            {
                var svc = LiveMonitorService.Instance;
                if (!App.MonitorActive)
                {
                    svc.Start(data.Application);
                    App.MonitorActive = true;
                    App.RefreshMonitorButton();
                    // Toast is shown inside svc.Start() → RunInitialScan()
                }
                else
                {
                    svc.Stop();                      // emits its own "stopped" toast
                    App.MonitorActive = false;
                    App.RefreshMonitorButton();
                }
                return Result.Succeeded;
            }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  4. DASHBOARD
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DashboardCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try { ClashDashboard.Instance.ShowWindow(); return Result.Succeeded; }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  5. SETTINGS
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string msg, ElementSet els)
        {
            try { new SettingsWindow().ShowDialog(); return Result.Succeeded; }
            catch (Exception ex) { msg = ex.Message; return Result.Failed; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  LEVEL PICKER DIALOG  — Issue 1 fix
    //  Shows all levels from the document. User picks one or selects "All Floors".
    // ══════════════════════════════════════════════════════════════════════

    internal class LevelPickerDialog : Window
    {
        private static WpfBrush BgMain  = B(15, 20, 32);
        private static WpfBrush BgCard  = B(22, 30, 48);
        private static WpfBrush AccBlue = B(41, 128, 185);
        private static WpfBrush TextW   = B(236, 240, 241);
        private static WpfBrush TextG   = B(127, 140, 141);

        private System.Windows.Controls.ListBox _listBox = null!;

        public bool   Confirmed         { get; private set; }
        public string SelectedLevelId   { get; private set; } = "";
        public string SelectedLevelName { get; private set; } = "All Floors";

        private List<(string id, string name, double elev)> _levels;

        public LevelPickerDialog(Document doc)
        {
            Title  = "ClashResolve AI — Select Floor to Scan";
            Width  = 420; Height = 480;
            Background = BgMain;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            _levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => (l.Id.Value.ToString(), l.Name,
                    UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Meters)))
                .ToList();

            BuildUI();
        }

        private void BuildUI()
        {
            var root = new WpfGrid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header
            var header = new StackPanel
            {
                Background = B(10, 15, 26),
                Margin     = new Thickness(0, 0, 0, 0)
            };
            header.Children.Add(new TextBlock
            {
                Text       = "Select Floor to Scan",
                FontSize   = 16, FontWeight = FontWeights.Bold,
                Foreground = TextW, Margin = new Thickness(20, 16, 20, 4)
            });
            header.Children.Add(new TextBlock
            {
                Text       = "Select a specific floor, or scan all floors at once.",
                FontSize   = 11, Foreground = TextG,
                Margin     = new Thickness(20, 0, 20, 14)
            });
            WpfGrid.SetRow(header, 0);
            root.Children.Add(header);

            // Level list
            _listBox = new System.Windows.Controls.ListBox
            {
                Background = BgCard,
                Foreground = TextW,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(12),
                FontSize = 12
            };

            // "All Floors" option always at top
            _listBox.Items.Add(new ListBoxItem
            {
                Content = "   All Floors  (full model scan)",
                Tag     = ("", "All Floors"),
                FontWeight = FontWeights.Bold,
                Foreground = new WpfBrush(WpfColor.FromRgb(41, 174, 128)),
                Padding    = new Thickness(8, 6, 8, 6)
            });

            // One entry per level
            foreach (var (id, name, elev) in _levels)
            {
                _listBox.Items.Add(new ListBoxItem
                {
                    Content = $"   {name}   ({elev:F2} m)",
                    Tag     = (id, name),
                    Padding = new Thickness(8, 5, 8, 5)
                });
            }

            _listBox.SelectedIndex = 0;
            WpfGrid.SetRow(_listBox, 1);
            root.Children.Add(_listBox);

            // Buttons
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12)
            };

            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 90, Height = 34,
                Background = B(44, 62, 80), Foreground = TextW,
                BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (s, e) => { Confirmed = false; Close(); };

            var okBtn = new Button
            {
                Content = "Scan Selected Floor", Width = 160, Height = 34,
                Background = AccBlue, Foreground = TextW,
                BorderThickness = new Thickness(0), FontWeight = FontWeights.Bold
            };
            okBtn.Click += (s, e) =>
            {
                var item = _listBox.SelectedItem as ListBoxItem;
                if (item?.Tag is ValueTuple<string, string> tag)
                {
                    SelectedLevelId   = tag.Item1;
                    SelectedLevelName = tag.Item2;
                }
                Confirmed = true;
                Close();
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            WpfGrid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            Content = root;
        }

        private static WpfBrush B(byte r, byte g, byte b) =>
            new WpfBrush(WpfColor.FromRgb(r, g, b));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  PROGRESS DIALOG
    // ══════════════════════════════════════════════════════════════════════

    internal class ProgressDialog : Window
    {
        private readonly TextBlock _tb;

        public ProgressDialog(string title)
        {
            Title  = title; Width = 500; Height = 120;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new WpfBrush(WpfColor.FromRgb(15, 20, 32));
            Topmost    = true;
            _tb = new TextBlock
            {
                Text = "Initialising…", FontSize = 12,
                Foreground = new WpfBrush(WpfColor.FromRgb(236, 240, 241)),
                Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap
            };
            Content = _tb;
        }

        public void SetMessage(string msg)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(() => _tb.Text = msg));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SETTINGS WINDOW
    // ══════════════════════════════════════════════════════════════════════

    internal class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            Title  = "ClashResolve AI v4.1 — Settings";
            Width  = 560; Height = 480;
            Background = new WpfBrush(WpfColor.FromRgb(15, 20, 32));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var s  = AppSettings.Load();
            var sp = new StackPanel { Margin = new Thickness(20) };

            sp.Children.Add(Lbl("Settings", 18, true));
            sp.Children.Add(new TextBlock { Height = 12 });

            sp.Children.Add(Lbl("OpenAI API Key", 11));
            var apiBox = new PasswordBox { Password = s.OpenAiApiKey, Height = 28,
                Margin = new Thickness(0, 4, 0, 12) };
            sp.Children.Add(apiBox);

            sp.Children.Add(Lbl("Active Rule Set", 11));
            var ruleCombo = new WpfComboBox { Height = 28, Margin = new Thickness(0, 4, 0, 12) };
            foreach (var rs in RulesEngine.GetAvailableRuleSets()) ruleCombo.Items.Add(rs);
            ruleCombo.SelectedItem = Session.RuleSetName;
            sp.Children.Add(ruleCombo);

            var linksCheck = new CheckBox
            {
                Content = "Scan Linked Models", IsChecked = s.ScanLinkedModels,
                Foreground = W(), Margin = new Thickness(0, 0, 0, 12)
            };
            sp.Children.Add(linksCheck);

            sp.Children.Add(Lbl("Rule Sets Folder:", 10));
            string rulesFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClashResolveAI", "Rules");
            sp.Children.Add(new TextBlock
            {
                Text = rulesFolder, Foreground = G(), FontSize = 10,
                Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap
            });
            var openFolderBtn = new Button
            {
                Content = "Open Rules Folder", Height = 28,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 16)
            };
            openFolderBtn.Click += (o, e) => System.Diagnostics.Process.Start(rulesFolder);
            sp.Children.Add(openFolderBtn);

            var saveBtn = new Button
            {
                Content = "Save Settings", Height = 36,
                Background = new WpfBrush(WpfColor.FromRgb(41, 128, 185)),
                Foreground = W(), BorderThickness = new Thickness(0)
            };
            saveBtn.Click += (o, e) =>
            {
                s.OpenAiApiKey     = apiBox.Password;
                s.ScanLinkedModels = linksCheck.IsChecked == true;
                AppSettings.Save(s);
                if (ruleCombo.SelectedItem is string rs) Session.RuleSetName = rs;
                MessageBox.Show("Settings saved.", "ClashResolve AI",
                    MessageBoxButton.OK);
                Close();
            };
            sp.Children.Add(saveBtn);
            Content = new ScrollViewer { Content = sp };
        }

        private static TextBlock Lbl(string t, int sz = 11, bool bold = false) =>
            new TextBlock
            {
                Text = t, FontSize = sz,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = G(), Margin = new Thickness(0, 0, 0, 2)
            };

        private static WpfBrush W() => new WpfBrush(WpfColor.FromRgb(236, 240, 241));
        private static WpfBrush G() => new WpfBrush(WpfColor.FromRgb(127, 140, 141));
    }
}
