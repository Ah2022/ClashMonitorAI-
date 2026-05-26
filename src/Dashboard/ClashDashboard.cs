// Dashboard/ClashDashboard.cs  — v4.0
//
// PROFESSIONAL IMPROVEMENT #13: Professional BIM Coordination Dashboard
//
// Sections:
//   1. SUMMARY CARDS   — Total Active / Resolved / Critical / Health Score
//   2. DISCIPLINE MATRIX — HVAC vs Plumbing = 37, etc.
//   3. TREND ANALYTICS — Week 1→427 clashes, Week 2→301, Week 3→118
//   4. GROUP VIEW      — Navisworks-style grouped issues (not raw flat list)
//   5. LIFECYCLE PANEL — update status, assign engineer, add comments
//
// Data now backed by ClashDatabase (SQLite) for persistence.

using Autodesk.Revit.DB;
using ClashResolveAI.Core;
using ClashResolveAI.Engine;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using WpfColor  = System.Windows.Media.Color;
using WpfBrush  = System.Windows.Media.SolidColorBrush;
using WpfGrid   = System.Windows.Controls.Grid;

namespace ClashResolveAI.Dashboard
{
    // ══════════════════════════════════════════════════════════════════════
    //  CLASH DASHBOARD  — singleton coordinator
    // ══════════════════════════════════════════════════════════════════════

    public class ClashDashboard
    {
        private static ClashDashboard? _instance;
        public  static ClashDashboard   Instance =>
            _instance ?? (_instance = new ClashDashboard());

        private readonly List<ClashResult> _clashes = new List<ClashResult>();
        private readonly List<ClashGroup>  _groups  = new List<ClashGroup>();
        private readonly ClashGroupingEngine _grouper = new ClashGroupingEngine();
        private DashboardWindow?           _window;

        public IReadOnlyList<ClashResult> Clashes => _clashes.AsReadOnly();
        public IReadOnlyList<ClashGroup>  Groups  => _groups.AsReadOnly();

        public void AddClashes(IEnumerable<ClashResult> clashes)
        {
            var list = clashes.ToList();
            _clashes.AddRange(list);

            // Cap session memory at 2000 (database holds the full history)
            if (_clashes.Count > 2000)
                _clashes.RemoveRange(0, _clashes.Count - 2000);

            // Persist to database
            ClashDatabase.Instance.BulkInsertClashes(list);

            // Re-group
            RebuildGroups();
            RefreshWindow();
        }

        public void AddFullScanResults(IEnumerable<ClashResult> clashes)
        {
            _clashes.Clear();
            _clashes.AddRange(clashes);
            ClashDatabase.Instance.BulkInsertClashes(clashes);
            ClashDatabase.Instance.SaveWeeklySnapshot();
            RebuildGroups();
            RefreshWindow();
        }

        public void UpdateClashStatus(string clashId, ClashStatus status,
            string author = "", string comment = "")
        {
            var c = _clashes.FirstOrDefault(x => x.ClashId == clashId);
            if (c != null) c.Status = status;
            ClashDatabase.Instance.UpdateClashStatus(clashId, status, author, comment);
            RebuildGroups();
            RefreshWindow();
        }

        private void RebuildGroups()
        {
            _groups.Clear();
            _groups.AddRange(_grouper.GroupClashes(_clashes));
        }

        private void RefreshWindow()
        {
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => _window?.Refresh(_clashes, _groups)));
        }

        public void ShowWindow()
        {
            if (_window == null || !_window.IsVisible)
            {
                _window = new DashboardWindow(_clashes, _groups);
                _window.Show();
            }
            else
            {
                _window.Activate();
            }
        }

        public void Clear()
        {
            _clashes.Clear();
            _groups.Clear();
        }

        public DashboardStats GetStats()
        {
            var db = ClashDatabase.Instance;
            return new DashboardStats
            {
                TotalClashes          = _clashes.Count,
                Critical              = _clashes.Count(c => c.Severity == ClashSeverity.Critical),
                Hard                  = _clashes.Count(c => c.Severity == ClashSeverity.Hard),
                Soft                  = _clashes.Count(c => c.Severity == ClashSeverity.Soft),
                ClearanceOnly         = _clashes.Count(c => c.Severity == ClashSeverity.Clearance),
                Resolved              = _clashes.Count(c => c.Status   == ClashStatus.Resolved || c.Status == ClashStatus.Closed),
                Open                  = _clashes.Count(c => c.Status   == ClashStatus.New      || c.Status == ClashStatus.Active),
                Ignored               = _clashes.Count(c => c.Status   == ClashStatus.Ignored),
                GroupCount            = _groups.Count,
                CoordinationHealthScore = db.GetHealthScore(),
                LastScan              = DateTime.Now,
                RecentClashes         = _clashes.Skip(Math.Max(0, _clashes.Count - 20)).ToList(),
                ActiveGroups          = _groups.Take(50).ToList(),
                DisciplineMatrix      = db.GetDisciplineMatrix(),
                Trends                = db.GetWeeklyTrends()
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DASHBOARD WINDOW  — WPF professional UI
    // ══════════════════════════════════════════════════════════════════════

    public class DashboardWindow : Window
    {
        // ── Colour palette ──────────────────────────────────────────────
        private static readonly WpfBrush BgMain    = Brush(15, 20, 32);
        private static readonly WpfBrush BgCard    = Brush(22, 30, 48);
        private static readonly WpfBrush BgHeader  = Brush(10, 15, 26);
        private static readonly WpfBrush AccentRed = Brush(192, 57, 43);
        private static readonly WpfBrush AccentOrg = Brush(211, 84, 0);
        private static readonly WpfBrush AccentYel = Brush(183, 149, 11);
        private static readonly WpfBrush AccentGrn = Brush(39, 174, 96);
        private static readonly WpfBrush AccentBlu = Brush(41, 128, 185);
        private static readonly WpfBrush TextMain  = Brush(236, 240, 241);
        private static readonly WpfBrush TextMuted = Brush(127, 140, 141);

        // ── UI references ───────────────────────────────────────────────
        private TextBlock _tbTotalClashes  = null!;
        private TextBlock _tbCritical      = null!;
        private TextBlock _tbResolved      = null!;
        private TextBlock _tbHealthScore   = null!;
        private TextBlock _tbGroupCount    = null!;
        private StackPanel _matrixPanel    = null!;
        private StackPanel _trendsPanel    = null!;
        private DataGrid   _groupGrid      = null!;
        private DataGrid   _clashGrid      = null!;
        private ICollectionView? _groupView;
        private ICollectionView? _clashView;
        private readonly Dictionary<string, string> _groupFilters = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _clashFilters = new Dictionary<string, string>();

        public DashboardWindow(List<ClashResult> clashes, List<ClashGroup> groups)
        {
            Title             = "ClashResolve AI v4.0 — Professional Coordination Dashboard";
            Width             = 1280;
            Height            = 820;
            Background        = BgMain;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode        = ResizeMode.CanResize;

            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Brush(0, 0, 128))); // Navy Blue
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.Black));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Brush(40, 50, 70)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(4)));
            headerStyle.Setters.Add(new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            
            Resources.Add(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), headerStyle);

            BuildUI();
            Refresh(clashes, groups);
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD UI
        // ════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            var root = new DockPanel { Background = BgMain, LastChildFill = true };

            // ── Header bar ────────────────────────────────────────────────
            var header = new Border
            {
                Background = BgHeader,
                Padding    = new Thickness(20, 12, 20, 12),
                Child      = new StackPanel { Orientation = Orientation.Horizontal,
                    Children = {
                        new TextBlock {
                            Text = "⬡ ClashResolve AI",
                            FontSize = 18, FontWeight = FontWeights.Bold,
                            Foreground = AccentBlu, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock {
                            Text = " — Professional BIM Coordination Dashboard",
                            FontSize = 14, Foreground = TextMuted,
                            VerticalAlignment = VerticalAlignment.Center }
                    }}
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // ── Summary cards row ─────────────────────────────────────────
            var cardsRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Background  = BgMain,
                Margin      = new Thickness(16, 16, 16, 8)
            };

            _tbTotalClashes = CardValue("?");
            _tbCritical     = CardValue("?");
            _tbResolved     = CardValue("?");
            _tbHealthScore  = CardValue("?");
            _tbGroupCount   = CardValue("?");

            cardsRow.Children.Add(SummaryCard("TOTAL ACTIVE",    _tbTotalClashes, AccentBlu));
            cardsRow.Children.Add(SummaryCard("CRITICAL",        _tbCritical,     AccentRed));
            cardsRow.Children.Add(SummaryCard("RESOLVED",        _tbResolved,     AccentGrn));
            cardsRow.Children.Add(SummaryCard("HEALTH SCORE",    _tbHealthScore,  AccentGrn));
            cardsRow.Children.Add(SummaryCard("ISSUE GROUPS",    _tbGroupCount,   AccentOrg));

            DockPanel.SetDock(cardsRow, Dock.Top);
            root.Children.Add(cardsRow);

            // ── Tab control — Groups | Clashes | Matrix | Trends ─────────
            var tabs = new TabControl
            {
                Background = BgMain,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(16, 0, 16, 16)
            };

            tabs.Items.Add(BuildGroupsTab());
            tabs.Items.Add(BuildClashesTab());
            tabs.Items.Add(BuildMatrixTab());
            tabs.Items.Add(BuildTrendsTab());

            root.Children.Add(tabs);
            Content = root;
        }

        // ── Tab: Grouped Issues ───────────────────────────────────────────
        private TabItem BuildGroupsTab()
        {
            _groupGrid = new DataGrid
            {
                AutoGenerateColumns  = false,
                IsReadOnly           = false,
                Background           = BgCard,
                Foreground           = TextMain,
                RowBackground        = BgCard,
                AlternatingRowBackground = Brush(28, 38, 58),
                GridLinesVisibility  = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brush(40, 50, 70),
                HeadersVisibility    = DataGridHeadersVisibility.Column,
                SelectionMode        = DataGridSelectionMode.Single,
                CanUserAddRows       = false,
                FontSize             = 12
            };

            _groupGrid.Columns.Add(Col("Severity",        "MaxSeverity",   70, _groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Count",           "Count",         55, _groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Group Title",     "GroupTitle",    340,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Level",           "LevelName",     100,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Grid",            "GridRef",        80,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Disc A",          "DisciplineA",    90,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Disc B",          "DisciplineB",    90,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Reason",          "GroupingReason", 140,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Primary Offender","PrimaryOffender",200,_groupFilters, () => _groupView?.Refresh()));
            _groupGrid.Columns.Add(Col("Status",          "Status",         80,_groupFilters, () => _groupView?.Refresh()));

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _groupGrid
            };

            var panel = new DockPanel();

            // Toolbar
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background  = BgHeader,
                Margin      = new Thickness(0, 0, 0, 4)
            };
            toolbar.Children.Add(DashBtn("⚡ Generate BCF", AccentBlu, () => ExportBcf()));
            toolbar.Children.Add(DashBtn("✓ Mark Selected Resolved", AccentGrn, () => MarkSelectedResolved()));
            toolbar.Children.Add(DashBtn("🔍 Navigate to Clash",     AccentOrg, () => NavigateToSelected()));
            DockPanel.SetDock(toolbar, Dock.Top);
            panel.Children.Add(toolbar);
            panel.Children.Add(scroll);

            return new TabItem
            {
                Header     = "⚠ Issue Groups",
                Background = BgCard,
                Foreground = TextMain,
                Content    = panel
            };
        }

        // ── Tab: Individual Clashes ───────────────────────────────────────
        private TabItem BuildClashesTab()
        {
            _clashGrid = new DataGrid
            {
                AutoGenerateColumns  = false,
                IsReadOnly           = true,
                Background           = BgCard,
                Foreground           = TextMain,
                RowBackground        = BgCard,
                AlternatingRowBackground = Brush(28, 38, 58),
                GridLinesVisibility  = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brush(40, 50, 70),
                HeadersVisibility    = DataGridHeadersVisibility.Column,
                SelectionMode        = DataGridSelectionMode.Single,
                CanUserAddRows       = false,
                FontSize             = 11
            };

            _clashGrid.Columns.Add(Col("ID",        "ClashId",     70, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Sev",       "Severity",    60, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Status",    "Status",      70, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Type",      "ClashType",   120,_clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Gap mm",    "GapMM",        60, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Vol mm³",   "OverlapVolumeMM3", 70,_clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Disc A",    "DisciplineA", 90, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Disc B",    "DisciplineB", 90, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Level",     "LevelName",   90, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Grid",      "GridRef",     70, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Moving",    "MovingDiscipline", 90,_clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Priority",  "Priority",    70, _clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Rule",      "RuleApplied", 120,_clashFilters, () => _clashView?.Refresh()));
            _clashGrid.Columns.Add(Col("Location",  "LocationText",200,_clashFilters, () => _clashView?.Refresh()));

            return new TabItem
            {
                Header  = "📋 All Clashes",
                Background = BgCard, Foreground = TextMain,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _clashGrid
                }
            };
        }

        // ── Tab: Discipline Matrix ────────────────────────────────────────
        private TabItem BuildMatrixTab()
        {
            _matrixPanel = new StackPanel { Background = BgCard, Margin = new Thickness(16) };
            return new TabItem
            {
                Header  = "🔢 Discipline Matrix",
                Background = BgCard, Foreground = TextMain,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _matrixPanel
                }
            };
        }

        // ── Tab: Trend Analytics ──────────────────────────────────────────
        private TabItem BuildTrendsTab()
        {
            _trendsPanel = new StackPanel { Background = BgCard, Margin = new Thickness(16) };

            _trendsPanel.Children.Add(new TextBlock
            {
                Text = "Coordination Trend",
                FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = TextMain, Margin = new Thickness(0, 0, 0, 12)
            });

            return new TabItem
            {
                Header  = "📈 Trend Analytics",
                Background = BgCard, Foreground = TextMain,
                Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _trendsPanel
                }
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  REFRESH
        // ════════════════════════════════════════════════════════════════

        public void Refresh(List<ClashResult> clashes, List<ClashGroup> groups)
        {
            try
            {
                var stats = ClashDashboard.Instance.GetStats();

                // Update summary cards
                _tbTotalClashes.Text = stats.Open.ToString();
                _tbCritical.Text     = stats.Critical.ToString();
                _tbResolved.Text     = stats.Resolved.ToString();
                _tbHealthScore.Text  = $"{stats.CoordinationHealthScore:F0}%";
                _tbGroupCount.Text   = groups.Count.ToString();

                // Color health score
                _tbHealthScore.Foreground = stats.CoordinationHealthScore >= 70 ? AccentGrn :
                                           stats.CoordinationHealthScore >= 40 ? AccentYel : AccentRed;

                // Update group grid
                _groupView = System.Windows.Data.CollectionViewSource.GetDefaultView(groups);
                _groupView.Filter = (obj) => {
                    if (!(obj is ClashGroup g)) return false;
                    foreach (var kv in _groupFilters) {
                        if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                        var prop = g.GetType().GetProperty(kv.Key);
                        var val  = prop?.GetValue(g)?.ToString() ?? "";
                        if (!val.Contains(kv.Value)) return false;
                    }
                    return true;
                };
                _groupGrid.ItemsSource = _groupView;

                // Update clash grid (most recent 500)
                var recentClashes = clashes.Skip(Math.Max(0, clashes.Count - 500)).ToList();
                _clashView = System.Windows.Data.CollectionViewSource.GetDefaultView(recentClashes);
                _clashView.Filter = (obj) => {
                    if (!(obj is ClashResult c)) return false;
                    foreach (var kv in _clashFilters) {
                        if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                        var prop = c.GetType().GetProperty(kv.Key);
                        var val  = prop?.GetValue(c)?.ToString() ?? "";
                        if (!val.Contains(kv.Value)) return false;
                    }
                    return true;
                };
                _clashGrid.ItemsSource = _clashView;

                // Update discipline matrix
                RefreshMatrix(stats.DisciplineMatrix);

                // Update trends
                RefreshTrends(stats.Trends);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Dashboard] Refresh error: {ex.Message}");
            }
        }

        private void RefreshMatrix(Dictionary<string, int> matrix)
        {
            _matrixPanel.Children.Clear();

            _matrixPanel.Children.Add(new TextBlock
            {
                Text = "Clash Distribution by Discipline Pair",
                FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = TextMain, Margin = new Thickness(0, 0, 0, 12)
            });

            if (!matrix.Any())
            {
                _matrixPanel.Children.Add(new TextBlock
                {
                    Text = "No discipline matrix data. Run a full scan to populate.",
                    Foreground = TextMuted, FontStyle = FontStyles.Italic
                });
                return;
            }

            // Sort by count descending
            foreach (var kv in matrix.OrderByDescending(kv => kv.Value))
            {
                string pair = kv.Key;
                int count = kv.Value;
                double barWidth = Math.Min(400, count * 4.0);
                WpfBrush barColor = count > 50 ? AccentRed :
                                    count > 20 ? AccentOrg :
                                    count > 5  ? AccentYel : AccentGrn;

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 3, 0, 3)
                };
                row.Children.Add(new TextBlock
                {
                    Text = pair, Width = 280, Foreground = TextMain,
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new Border
                {
                    Width = barWidth, Height = 18,
                    Background = barColor, CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(4, 0, 8, 0)
                });
                row.Children.Add(new TextBlock
                {
                    Text = count.ToString(),
                    Foreground = barColor, FontWeight = FontWeights.Bold,
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                });
                _matrixPanel.Children.Add(row);
            }
        }

        private void RefreshTrends(List<WeeklyTrend> trends)
        {
            // Remove old trend bars (keep header)
            while (_trendsPanel.Children.Count > 1)
                _trendsPanel.Children.RemoveAt(1);

            if (!trends.Any())
            {
                _trendsPanel.Children.Add(new TextBlock
                {
                    Text = "Not enough data for trend analysis. Run weekly scans to build history.",
                    Foreground = TextMuted, FontStyle = FontStyles.Italic
                });
                return;
            }

            int maxCount = trends.Max(t => t.ClashCount);
            if (maxCount == 0) maxCount = 1;

            foreach (var t in trends)
            {
                double barW = Math.Max(4, t.ClashCount * 350.0 / maxCount);
                double resW = t.Resolved > 0 ? Math.Max(2, t.Resolved * 350.0 / maxCount) : 0;

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin      = new Thickness(0, 5, 0, 5),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(new TextBlock
                {
                    Text  = t.WeekLabel, Width = 90,
                    Foreground = TextMuted, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var barStack = new StackPanel { Orientation = Orientation.Vertical };
                barStack.Children.Add(new Border
                {
                    Width = barW, Height = 14, Background = AccentBlu,
                    CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 0, 1)
                });
                if (resW > 0)
                    barStack.Children.Add(new Border
                    {
                        Width = resW, Height = 8, Background = AccentGrn,
                        CornerRadius = new CornerRadius(2)
                    });
                row.Children.Add(barStack);
                row.Children.Add(new TextBlock
                {
                    Text = $"  {t.ClashCount} total / {t.Resolved} resolved",
                    Foreground = TextMain, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
                _trendsPanel.Children.Add(row);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  TOOLBAR ACTIONS
        // ════════════════════════════════════════════════════════════════

        private void ExportBcf()
        {
            try
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select output folder for BCF export"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                var exporter = new Services.BcfExportService();
                string path  = exporter.ExportGroups(
                    ClashDashboard.Instance.Groups.ToList(),
                    "ClashResolveAI",
                    dlg.SelectedPath);

                MessageBox.Show($"BCF exported:\n{path}", "BCF Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"BCF export failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MarkSelectedResolved()
        {
            if (_groupGrid.SelectedItem is ClashGroup group)
            {
                foreach (var c in group.Clashes)
                    ClashDashboard.Instance.UpdateClashStatus(c.ClashId, ClashStatus.Resolved, "User", "Marked resolved via dashboard");
                MessageBox.Show($"Group '{group.GroupTitle}' marked as Resolved.", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void NavigateToSelected()
        {
            if (_groupGrid.SelectedItem is ClashGroup group && group.Clashes.Any())
            {
                var clash = group.Clashes[0];
                MessageBox.Show(
                    $"Clash: {clash.ClashId}\n" +
                    $"Location: {clash.LocationText}\n" +
                    $"Level: {clash.LevelName}\n" +
                    $"Grid: {clash.GridRef}\n\n" +
                    "Use 'Navigate to Clash' viewpoint in Revit to jump to this location.",
                    "Clash Location", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  UI FACTORY HELPERS
        // ════════════════════════════════════════════════════════════════

        private static FrameworkElement SummaryCard(string label, TextBlock valueBlock, WpfBrush accentColor)
        {
            var border = new Border
            {
                Background    = BgCard,
                BorderBrush   = accentColor,
                BorderThickness = new Thickness(0, 0, 0, 3),
                CornerRadius  = new CornerRadius(6),
                Padding       = new Thickness(16, 12, 16, 12),
                Margin        = new Thickness(0, 0, 12, 0),
                MinWidth      = 150,
                Child         = new StackPanel
                {
                    Children = {
                        new TextBlock {
                            Text = label, FontSize = 10, FontWeight = FontWeights.Bold,
                            Foreground = TextMuted,
                            Margin = new Thickness(0, 0, 0, 6) },
                        valueBlock
                    }
                }
            };
            return border;
        }

        private static TextBlock CardValue(string text) =>
            new TextBlock
            {
                Text       = text,
                FontSize   = 28,
                FontWeight = FontWeights.Bold,
                Foreground = TextMain
            };

        private static Button DashBtn(string text, WpfBrush color, Action onClick)
        {
            var btn = new Button
            {
                Content    = text,
                Background = color,
                Foreground = TextMain,
                BorderThickness = new Thickness(0),
                Padding    = new Thickness(12, 6, 12, 6),
                Margin     = new Thickness(4, 4, 0, 4),
                FontSize   = 11,
                Cursor     = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private static DataGridTextColumn Col(string header, string binding, double width, 
            Dictionary<string, string> filters, Action onFilterChanged)
        {
            var col = new DataGridTextColumn
            {
                Binding = new System.Windows.Data.Binding(binding),
                Width   = width,
                CanUserSort = true
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0,2,0,2) };
            stack.Children.Add(new TextBlock { 
                Text = header, 
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center 
            });

            var filterBox = new TextBox { 
                Margin = new Thickness(4, 4, 4, 2), 
                Height = 18, 
                FontSize = 9,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(1)
            };
            filterBox.TextChanged += (s, e) => {
                filters[binding] = filterBox.Text;
                onFilterChanged();
            };
            stack.Children.Add(filterBox);

            col.Header = stack;
            return col;
        }

        private static WpfBrush Brush(byte r, byte g, byte b) =>
            new WpfBrush(WpfColor.FromRgb(r, g, b));
    }
}
