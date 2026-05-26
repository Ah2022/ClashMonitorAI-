// LiveMonitor/ClashRadarPanel.cs  — v5.0
//
// Floating WPF panel — real-time Clash Radar.
//
// Matches the UI from the ClashRadar video:
//   • "Clash Radar" header with live pulse indicator
//   • "Active Clashes" list:  # | Time | Category A | Category B | ID B | Location
//   • Category filter ComboBox (top-right of sub-header)
//   • Per-row severity colour bar (left edge: red / orange / yellow / blue)
//   • Action buttons: Show 3D | Show 2D | Ignore  |  Refresh | Export
//   • Mini 3D preview canvas at the bottom (updates on row selection)
//
// Thread safety:
//   DataChanged fires on Revit thread → dispatched to WPF UI thread here.
//   All Revit API calls go through ExternalEvent handlers (never direct).

using Autodesk.Revit.UI;
using ClashResolveAI.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using ComboBox = System.Windows.Controls.ComboBox;

namespace ClashResolveAI.LiveMonitor
{
    public class ClashRadarPanel : Window
    {
        // ── Singleton ──────────────────────────────────────────────────
        private static ClashRadarPanel? _instance;
        public static ClashRadarPanel  Instance  => _instance ??= new ClashRadarPanel();
        public static new bool             IsVisible => _instance?.IsLoaded == true
                                                 && _instance.Visibility == Visibility.Visible;

        // ── Revit integration (injected by LiveMonitorService) ─────────
        private ExternalEvent? _navEvent;
        private ExternalEvent? _refreshEvent;
        private readonly ClashNavHandler     _navHandler     = new ClashNavHandler();
        private readonly ClashRefreshHandler _refreshHandler = new ClashRefreshHandler();

        // ── Data ───────────────────────────────────────────────────────
        private List<ClashResult>                    _displayed     = new List<ClashResult>();
        private ClashResult?                         _selected;
        private string                               _catFilter     = AllCats;
        private const string                         AllCats        = "All Categories";

        // ── UI controls (populated in BuildUI) ────────────────────────
        private TextBlock    _badgeCount  = null!;
        private ComboBox     _catCombo    = null!;
        private StackPanel   _rowsPanel   = null!;
        private Canvas       _preview     = null!;
        private TextBlock    _statusLabel = null!;
        private Ellipse      _pulse       = null!;
        private Button       _btn3D       = null!;
        private Button       _btn2D       = null!;
        private Button       _btnIgnore   = null!;
        private Button       _btnRefresh  = null!;
        private Button       _btnExport   = null!;

        // ── Row tracking (for selection state) ────────────────────────
        private readonly Dictionary<ClashResult, Border> _rowMap = new Dictionary<ClashResult, Border>();

        // ── Pulse animation timer ──────────────────────────────────────
        private readonly DispatcherTimer _pulseTimer;
        private bool _pulseState;

        // ══════════════════════════════════════════════════════════════
        //  COLOUR PALETTE
        // ══════════════════════════════════════════════════════════════

        private static SolidColorBrush Bg       = SB(10,  15, 26);
        private static SolidColorBrush BgCard   = SB(14,  21, 36);
        private static SolidColorBrush BgHdr    = SB( 8,  12, 22);
        private static SolidColorBrush BgRow    = SB(17,  25, 42);
        private static SolidColorBrush BgSel    = SB(22,  42, 76);
        private static SolidColorBrush BgHover  = SB(20,  32, 56);
        private static SolidColorBrush Bdr      = SB(30,  45, 72);
        private static SolidColorBrush AccBlue  = SB(41, 128,185);
        private static SolidColorBrush AccOrang = SB(230,126, 34);
        private static SolidColorBrush AccGreen = SB( 39,174, 96);
        private static SolidColorBrush AccRed   = SB(192, 57, 43);
        private static SolidColorBrush AccYel   = SB(241,196, 15);
        private static SolidColorBrush TxtW     = SB(236,240,241);
        private static SolidColorBrush TxtG     = SB(127,140,141);
        private static SolidColorBrush Transp   = new SolidColorBrush(Colors.Transparent);

        private static SolidColorBrush SB(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));

        // ══════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════

        public ClashRadarPanel()
        {
            Title               = "Clash Radar";
            Width               = 380; Height = 640;
            MinWidth            = 340; MinHeight = 420;
            Background          = Bg;
            BorderBrush         = Bdr;
            BorderThickness     = new Thickness(1);
            WindowStyle         = WindowStyle.None;
            AllowsTransparency  = false;
            ResizeMode          = ResizeMode.CanResizeWithGrip;
            Topmost             = false;
            ShowInTaskbar       = false;

            var wa = SystemParameters.WorkArea;
            Left = wa.Right  - Width  - 14;
            Top  = wa.Top    + 40;

            BuildUI();

            // Subscribe to data store — fires on Revit thread, dispatch to UI
            RadarDataStore.Instance.DataChanged += (s, e) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(RefreshList));

            // Pulse animation
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            _pulseTimer.Tick += (s, e) =>
            {
                _pulseState = !_pulseState;
                _pulse.Opacity = _pulseState ? 1.0 : 0.35;
            };
            _pulseTimer.Start();

            Closed += (s, e) => { _instance = null; _pulseTimer.Stop(); };
        }

        // ── Revit wiring (called by LiveMonitorService after ExternalEvent creation) ──
        public void SetRevitEvents(ExternalEvent navEvent, ExternalEvent refreshEvent)
        {
            _navEvent     = navEvent;
            _refreshEvent = refreshEvent;
        }

        // ══════════════════════════════════════════════════════════════
        //  UI CONSTRUCTION
        // ══════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            var root = new Grid();
            // Rows: Header | SubHeader | ColHeaders | List(*) | BtnRow1 | BtnRow2 | Preview
            foreach (var h in new[] { 44.0, 42.0, 28.0, -1.0, 42.0, 42.0, 148.0 })
            {
                root.RowDefinitions.Add(h < 0
                    ? new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
                    : new RowDefinition { Height = new GridLength(h) });
            }

            int row = 0;
            void Add(UIElement el, int r) { Grid.SetRow(el, r); root.Children.Add(el); }

            Add(MakeHeader(),      row++);
            Add(MakeSubHeader(),   row++);
            Add(MakeColHeaders(),  row++);
            Add(MakeList(),        row++);
            Add(MakeBtnRow1(),     row++);
            Add(MakeBtnRow2(),     row++);
            Add(MakePreview(),     row++);

            Content = root;
        }

        // ── Header ─────────────────────────────────────────────────────

        private Border MakeHeader()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            // Pulse dot
            _pulse = new Ellipse { Width = 10, Height = 10, Fill = AccGreen,
                                   VerticalAlignment = VerticalAlignment.Center,
                                   HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(_pulse, 0);

            // Title
            var title = new TextBlock { Text = "Clash Radar",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = TxtW, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);

            // Status label (shows "ACTIVE" or "IDLE")
            _statusLabel = new TextBlock
            {
                Text = "● ACTIVE", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = AccGreen,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(_statusLabel, 2);

            // Close button
            var closeBtn = Btn("✕", Transp, TxtG, 24, 24);
            closeBtn.FontSize = 11;
            closeBtn.Cursor   = Cursors.Hand;
            closeBtn.Click   += (s, e) => Hide();
            Grid.SetColumn(closeBtn, 2);

            // Stack status + close
            var rightGrid = new Grid();
            rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rightGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            Grid.SetColumn(_statusLabel, 0);
            Grid.SetColumn(closeBtn, 1);
            rightGrid.Children.Add(_statusLabel);
            rightGrid.Children.Add(closeBtn);
            Grid.SetColumn(rightGrid, 2);

            g.Children.Add(_pulse);
            g.Children.Add(title);
            g.Children.Add(rightGrid);

            var border = new Border
            {
                Background      = BgHdr,
                BorderBrush     = Bdr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding         = new Thickness(10, 0, 8, 0),
                Child           = g
            };
            border.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            return border;
        }

        // ── Sub-header (badge + filter) ─────────────────────────────────

        private FrameworkElement MakeSubHeader()
        {
            var g = new Grid { Background = BgCard };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // "Active Clashes" + badge
            var leftStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            leftStack.Children.Add(new TextBlock
            {
                Text = "Active Clashes", FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = TxtG, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _badgeCount = new TextBlock
            {
                Text = "0", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = TxtW, Background = AccBlue,
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            leftStack.Children.Add(_badgeCount);
            Grid.SetColumn(leftStack, 0);

            // Category filter
            _catCombo = new ComboBox
            {
                Width = 148, Height = 26,
                Background = Bg, Foreground = TxtW, BorderBrush = Bdr,
                FontSize = 10, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _catCombo.Items.Add(AllCats);
            _catCombo.SelectedIndex = 0;
            _catCombo.SelectionChanged += (s, e) =>
            {
                _catFilter = _catCombo.SelectedItem as string ?? AllCats;
                RefreshList();
            };
            Grid.SetColumn(_catCombo, 1);

            g.Children.Add(leftStack);
            g.Children.Add(_catCombo);

            // Bottom separator
            var bdr = new Border { BorderBrush = Bdr, BorderThickness = new Thickness(0,0,0,1) };
            bdr.Child = g;
            return bdr;
        }

        // ── Column headers ──────────────────────────────────────────────

        private Border MakeColHeaders()
        {
            var g = ColGrid();
            var labels = new[] { "#", "Time", "Disciplines", "IDs", "Category A", "Category B", "Location" };
            for (int i = 0; i < labels.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = labels[i], FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = TxtG, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(ColLeft(i), 0, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(tb, i);
                g.Children.Add(tb);
            }
            return new Border
            {
                Background = SB(12, 18, 32),
                BorderBrush = Bdr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = g
            };
        }

        // ── Clash list (scrollable) ─────────────────────────────────────

        private ScrollViewer MakeList()
        {
            _rowsPanel = new StackPanel { Background = Bg };
            return new ScrollViewer
            {
                Content = _rowsPanel,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Bg
            };
        }

        // ── Action buttons row 1: Show 3D | Show 2D | Ignore ───────────

        private Grid MakeBtnRow1()
        {
            _btn3D     = Btn("Show 3D", AccBlue,          TxtW);
            _btn2D     = Btn("Show 2D", SB(39,174,96),   TxtW);
            _btnIgnore = Btn("✕ Ignore", SB(52,73,94),   TxtG);

            _btn3D.Click     += OnShow3D;
            _btn2D.Click     += OnShow2D;
            _btnIgnore.Click += OnIgnore;

            return EqualGrid(new[] { _btn3D, _btn2D, _btnIgnore },
                SB(12, 18, 32), new Thickness(0,0,0,1));
        }

        // ── Action buttons row 2: Refresh | Export ──────────────────────

        private Grid MakeBtnRow2()
        {
            _btnRefresh = Btn("↺ Refresh", SB(52,73,94), TxtW);
            _btnExport  = Btn("⬇ Export",  SB(52,73,94), TxtG);

            _btnRefresh.Click += OnRefresh;
            _btnExport.Click  += OnExport;

            return EqualGrid(new[] { _btnRefresh, _btnExport },
                BgCard, new Thickness(0,0,0,1));
        }

        // ── Mini preview canvas ──────────────────────────────────────────

        private Canvas MakePreview()
        {
            _preview = new Canvas { Background = SB(8, 12, 22) };
            _preview.SizeChanged += (s, e) => DrawPreview(_selected);
            DrawPreview(null);
            return _preview;
        }

        // ══════════════════════════════════════════════════════════════
        //  DATA → UI BINDING
        // ══════════════════════════════════════════════════════════════

        private void RefreshList()
        {
            // Refresh category filter dropdown
            var cats = RadarDataStore.Instance.GetCategories();
            var prev = _catCombo.SelectedItem as string ?? AllCats;
            _catCombo.Items.Clear();
            _catCombo.Items.Add(AllCats);
            foreach (var c in cats) _catCombo.Items.Add(c);
            _catCombo.SelectedItem = _catCombo.Items.Contains(prev) ? prev : AllCats;

            // Get filtered data
            var all = RadarDataStore.Instance.GetActive();
            _displayed = _catFilter == AllCats
                ? all
                : all.Where(c =>
                    (c.ElementA?.Category?.Name ?? c.DisciplineA.ToString()).Equals(_catFilter, StringComparison.OrdinalIgnoreCase) ||
                    (c.ElementB?.Category?.Name ?? c.DisciplineB.ToString()).Equals(_catFilter, StringComparison.OrdinalIgnoreCase))
                  .ToList();

            // Update count badge
            _badgeCount.Text = _displayed.Count.ToString();

            // Rebuild rows
            _rowsPanel.Children.Clear();
            _rowMap.Clear();

            for (int i = 0; i < _displayed.Count; i++)
                _rowsPanel.Children.Add(MakeRow(i + 1, _displayed[i]));

            // Restore selection if still present
            if (_selected != null && !_displayed.Contains(_selected))
            {
                _selected = null;
                DrawPreview(null);
                UpdateActionButtons();
            }
        }

        // ── Build a single clash row ────────────────────────────────────

        private Border MakeRow(int index, ClashResult clash)
        {
            bool   isSelected = clash == _selected;
            string catA = clash.ElementA?.Category?.Name ?? clash.DisciplineA.ToString();
            string catB = clash.ElementB?.Category?.Name ?? clash.DisciplineB.ToString();
            string disc = $"{clash.DisciplineA} / {clash.DisciplineB}";
            
            string idA  = clash.ElementA?.Id.Value.ToString() ?? "";
            string idB  = clash.ElementB?.Id.Value.ToString() ?? "";
            string idDisp = $"{idA}:{idB}";
            
            string loc  = string.IsNullOrWhiteSpace(clash.LocationText)
                        ? (string.IsNullOrWhiteSpace(clash.GridRef) ? "—" : clash.GridRef)
                        : clash.LocationText;
            string time = DateTime.Now.ToString("h:mm tt");

            // Severity accent bar colour (left 4px)
            var sevColor = clash.Severity switch
            {
                ClashSeverity.Critical  => AccRed,
                ClashSeverity.Hard      => AccOrang,
                ClashSeverity.Soft      => AccYel,
                ClashSeverity.Clearance => AccBlue,
                _                       => AccOrang
            };

            // ── Outer border with severity left bar ────────────────────
            var outer = new Border
            {
                Background      = isSelected ? BgSel : BgRow,
                BorderBrush     = Bdr,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Margin          = new Thickness(0, 0, 0, 0),
                Cursor          = Cursors.Hand
            };

            var innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });   // severity bar
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Severity colour bar
            var sevBar = new Border
            {
                Background = sevColor,
                Width      = 4
            };
            Grid.SetColumn(sevBar, 0);

            // Data columns
            var dataGrid = ColGrid();

            void Cell(int col, string text, SolidColorBrush fg, bool bold = false)
            {
                var tb = new TextBlock
                {
                    Text         = text,
                    FontSize     = 10,
                    FontWeight   = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground   = fg,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin       = new Thickness(ColLeft(col), 0, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(tb, col);
                dataGrid.Children.Add(tb);
            }

            Cell(0, index.ToString(), TxtG);
            Cell(1, time,  TxtG);
            Cell(2, disc,  TxtW);
            Cell(3, idDisp, TxtW, bold: isSelected);
            Cell(4, catA,  isSelected ? AccOrang : SB(198,140,74));
            Cell(5, catB,  isSelected ? AccBlue  : SB(82,130,170));
            Cell(6, loc,   TxtG);

            Grid.SetColumn(sevBar,   0);
            Grid.SetColumn(dataGrid, 1);
            innerGrid.Children.Add(sevBar);
            innerGrid.Children.Add(dataGrid);
            outer.Child = innerGrid;

            // Hover + click
            outer.MouseEnter  += (s, e) => { if (clash != _selected) outer.Background = BgHover; };
            outer.MouseLeave  += (s, e) => { if (clash != _selected) outer.Background = BgRow; };
            outer.MouseLeftButtonDown += (s, e) => SelectClash(clash);

            _rowMap[clash] = outer;
            return outer;
        }

        // ── Select a clash row ──────────────────────────────────────────

        private void SelectClash(ClashResult clash)
        {
            // Deselect previous
            if (_selected != null && _rowMap.TryGetValue(_selected, out var prevBorder))
                prevBorder.Background = BgRow;

            _selected = clash;

            // Highlight new selection
            if (_rowMap.TryGetValue(clash, out var newBorder))
                newBorder.Background = BgSel;

            DrawPreview(clash);
            UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            bool has = _selected != null;
            double alpha = has ? 1.0 : 0.45;
            foreach (var b in new[] { _btn3D, _btn2D, _btnIgnore })
                b.Opacity = alpha;
        }

        // ══════════════════════════════════════════════════════════════
        //  MINI PREVIEW  — draws two crossing bars for the selected clash
        // ══════════════════════════════════════════════════════════════

        private void DrawPreview(ClashResult? clash)
        {
            _preview.Children.Clear();

            double W = _preview.ActualWidth;
            double H = _preview.ActualHeight;
            if (W < 10 || H < 10) return;   // not laid out yet

            // Background fill with subtle gradient or dark color
            var bgRect = new Rectangle { Width = W, Height = H,
                Fill = SB(8, 12, 22) };
            Canvas.SetLeft(bgRect, 0); Canvas.SetTop(bgRect, 0);
            _preview.Children.Add(bgRect);

            if (clash == null)
            {
                var hint = new TextBlock
                {
                    Text       = "Select a clash to load preview…",
                    FontSize   = 10,
                    Foreground = TxtG
                };
                Canvas.SetLeft(hint, W / 2 - 82);
                Canvas.SetTop(hint, H / 2 - 8);
                _preview.Children.Add(hint);
                return;
            }

            // Subtle grid lines
            DrawGridLines(W, H);

            string catA = clash.ElementA?.Category?.Name ?? clash.DisciplineA.ToString();
            string catB = clash.ElementB?.Category?.Name ?? clash.DisciplineB.ToString();

            // ── 3D Perspective Projection (Simulated) ──────────────
            double cx = W / 2, cy = H / 2;
            
            // Draw Element B (Structural/Target) — Vertical/Diagonal
            var colB = CategoryColor(catB);
            var beam = new Polygon {
                Points = new PointCollection {
                    new Point(cx - 60, cy + 20),
                    new Point(cx + 20, cy - 40),
                    new Point(cx + 35, cy - 35),
                    new Point(cx - 45, cy + 25)
                },
                Fill = new SolidColorBrush(colB),
                Opacity = 0.7,
                Stroke = TxtW, StrokeThickness = 0.5
            };
            _preview.Children.Add(beam);

            // Draw Element A (MEP/Source) — Horizontal/Crossing
            var colA = CategoryColor(catA);
            var pipe = new Polygon {
                Points = new PointCollection {
                    new Point(cx - 70, cy - 10),
                    new Point(cx + 70, cy + 10),
                    new Point(cx + 70, cy + 25),
                    new Point(cx - 70, cy + 5)
                },
                Fill = new SolidColorBrush(colA),
                Opacity = 0.9,
                Stroke = TxtW, StrokeThickness = 0.8
            };
            _preview.Children.Add(pipe);

            // ── Clash intersection marker (Glow effect) ─────────────────────────────
            var glow = new Ellipse {
                Width = 24, Height = 24,
                Fill = new SolidColorBrush(SeverityColor(clash.Severity)) { Opacity = 0.3 }
            };
            Canvas.SetLeft(glow, cx - 12); Canvas.SetTop(glow, cy - 12);
            _preview.Children.Add(glow);

            var marker = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(SeverityColor(clash.Severity)),
                Stroke = TxtW, StrokeThickness = 1.5
            };
            Canvas.SetLeft(marker, cx - 6);
            Canvas.SetTop(marker, cy - 6);
            _preview.Children.Add(marker);

            // ── Labels ────────────────────────────────────────────────
            AddLabel(TruncateCat(catA), colA, 20, cy - 30);
            AddLabel(TruncateCat(catB), colB, cx + 40, H - 35);

            // Gap/severity text
            string sevText = clash.Severity == ClashSeverity.Hard && clash.GapMM < 0
                ? $"{clash.Severity} ({Math.Abs(clash.GapMM):F0}mm overlap)"
                : $"{clash.Severity}  gap: {clash.GapMM:F0}mm";
            AddLabel(sevText, SeverityColor(clash.Severity), 12, 10, 10);
        }

        private void AddLabel(string text, Color col, double x, double y, double size = 9.5)
        {
            var tb = new TextBlock
            {
                Text       = text,
                FontSize   = size,
                Foreground = new SolidColorBrush(col),
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            _preview.Children.Add(tb);
        }

        private void DrawGridLines(double W, double H)
        {
            var gridColor = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
            double step = 30;
            for (double x = step; x < W; x += step)
            {
                var l = new Line { X1=x, Y1=0, X2=x, Y2=H,
                    Stroke=gridColor, StrokeThickness=0.5 };
                _preview.Children.Add(l);
            }
            for (double y = step; y < H; y += step)
            {
                var l = new Line { X1=0, Y1=y, X2=W, Y2=y,
                    Stroke=gridColor, StrokeThickness=0.5 };
                _preview.Children.Add(l);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  BUTTON HANDLERS
        // ══════════════════════════════════════════════════════════════

        private void OnShow3D(object s, RoutedEventArgs e)
        {
            if (_selected == null || _navEvent == null) return;
            _navHandler.Target = _selected;
            _navHandler.Mode   = NavMode.View3D;
            _navEvent.Raise();
        }

        private void OnShow2D(object s, RoutedEventArgs e)
        {
            if (_selected == null || _navEvent == null) return;
            _navHandler.Target = _selected;
            _navHandler.Mode   = NavMode.View2D;
            _navEvent.Raise();
        }

        private void OnIgnore(object s, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RadarDataStore.Instance.IgnoreClash(_selected);
            _selected = null;
            DrawPreview(null);
            UpdateActionButtons();
        }

        private void OnRefresh(object s, RoutedEventArgs e)
        {
            _btnRefresh.IsEnabled = false;
            _btnRefresh.Content   = "…";
            _refreshEvent?.Raise();
            // Re-enable after 2 seconds
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (ts, te) => { t.Stop(); _btnRefresh.IsEnabled = true; _btnRefresh.Content = "↺ Refresh"; };
            t.Start();
        }

        private void OnExport(object s, RoutedEventArgs e)
        {
            RadarExporter.ExportToCsv(RadarDataStore.Instance.GetActive());
        }

        // ══════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════

        /// <summary>Creates a Grid with the standard column widths for data rows.</summary>
        private static Grid ColGrid()
        {
            var g = new Grid();
            // # | Time | Disc | IDs | Cat A | Cat B | Location
            double[] ws = { 26, 54, 70, 90, 70, 70, 0 };
            foreach (var w in ws)
                g.ColumnDefinitions.Add(w > 0
                    ? new ColumnDefinition { Width = new GridLength(w) }
                    : new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return g;
        }

        private static double ColLeft(int col) => col == 0 ? 6 : 4;

        private static Grid EqualGrid(Button[] buttons, SolidColorBrush bg,
                                       Thickness borderThickness)
        {
            var g = new Grid { Background = bg };
            for (int i = 0; i < buttons.Length; i++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Margin = new Thickness(4, 6, 4, 6);
                Grid.SetColumn(buttons[i], i);
                g.Children.Add(buttons[i]);
            }
            return g;
        }

        private static Button Btn(string text, SolidColorBrush bg, SolidColorBrush fg,
                                   double? w = null, double? h = null)
        {
            var b = new Button
            {
                Content         = text,
                Background      = bg,
                Foreground      = fg,
                BorderThickness = new Thickness(0),
                FontSize        = 10,
                FontWeight      = FontWeights.SemiBold,
                Height          = h ?? 28,
                Cursor          = Cursors.Hand
            };
            if (w.HasValue) b.Width = w.Value;
            return b;
        }

        private static Color CategoryColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return Color.FromRgb(127,140,141);
            string l = category.ToLowerInvariant();
            if (l.Contains("struct") || l.Contains("wall") || l.Contains("column") || l.Contains("beam") || l.Contains("floor"))
                return Color.FromRgb(41,128,185);
            if (l.Contains("mech") || l.Contains("hvac") || l.Contains("duct") || l.Contains("vent"))
                return Color.FromRgb(142,68,173);
            if (l.Contains("plumb") || l.Contains("pipe") || l.Contains("drain"))
                return Color.FromRgb(39,174,96);
            if (l.Contains("cable") || l.Contains("tray") || l.Contains("conduit") || l.Contains("elec"))
                return Color.FromRgb(230,126,34);
            if (l.Contains("fire"))
                return Color.FromRgb(192,57,43);
            return Color.FromRgb(127,140,141);
        }

        private static Color SeverityColor(ClashSeverity s) => s switch
        {
            ClashSeverity.Critical  => Color.FromRgb(192, 57, 43),
            ClashSeverity.Hard      => Color.FromRgb(230,126, 34),
            ClashSeverity.Soft      => Color.FromRgb(241,196, 15),
            ClashSeverity.Clearance => Color.FromRgb( 41,128,185),
            _                       => Color.FromRgb(127,140,141)
        };

        private static string TruncateCat(string s) =>
            s.Length > 14 ? s.Substring(0, 13) + "…" : s;

        // ── Public state control (called by LiveMonitorService) ──────────

        public void SetActiveState(bool active)
        {
            _statusLabel.Text       = active ? "● ACTIVE" : "● IDLE";
            _statusLabel.Foreground = active ? AccGreen : TxtG;
            _pulse.Fill             = active ? AccGreen : TxtG;
            if (active) _pulseTimer.Start(); else _pulseTimer.Stop();
        }
    }
}
