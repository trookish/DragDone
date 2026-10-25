using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Native;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DragDone.UI;

/// <summary>What the overlay needs from the rest of the app.</summary>
public interface IOverlayHost
{
    Settings Settings { get; }
    Capabilities Capabilities();

    /// <summary>Actions that need UI (share, shelf, terminal, free crop, browse).</summary>
    void RunInteractive(string actionId, DragPayload payload);

    void RememberUndo(UndoRecord record);
}

/// <summary>
/// The radial menu window: layered, topmost, never activated, an OLE drop target.
/// Transparent pixels are click- and drop-through, so dragging off the disc lands on
/// whatever is underneath as usual.
/// </summary>
public sealed class OverlayWindow : Window
{
    /// <summary>Gap between the ring's edge and the caption band underneath it.</summary>
    const double BelowGap = 10;

    /// <summary>Height of the file-info pill that sits above the ring.</summary>
    const double InfoPillHeight = 30;

    double _canvasW = 420, _canvasH = 520;
    double MenuLeft => (_canvasW - _menu.Geometry.Size) / 2;
    /// <summary>Top of the ring. Room above it is reserved for the file-info pill.</summary>
    double MenuTop => InfoPillHeight + 8;
    Point HubCenterInWindow => new(MenuLeft + _menu.Geometry.Center.X, MenuTop + _menu.Geometry.Center.Y);

    readonly IOverlayHost _host;
    readonly RadialMenu _menu = new();
    readonly Canvas _root = new();
    readonly Border _tabsBar = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<MenuTab, Border> _pills = new();
    readonly Border _captionBox = new();
    readonly StackPanel _below = new();
    readonly TextBlock _caption = new();
    readonly Border _chip = new();
    readonly Border _infoPill = new();
    readonly StackPanel _infoRow = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _infoTitle = new();
    readonly TextBlock _infoMeta = new();
    readonly ScaleTransform _scale = new(1, 1);
    readonly DispatcherTimer _dwellTimer;
    readonly DispatcherTimer _spinTimer;
    readonly DispatcherTimer _hideTimer;

    DragPayload? _payload;
    MenuTab _tab = MenuTab.Convert;
    readonly Stack<ActionDef> _path = new();
    IReadOnlyList<ActionDef> _ring = Array.Empty<ActionDef>();
    DragDropKeyStates _lastMods;
    bool _dropped;
    bool _busy;
    bool _visible;
    object? _dwellTarget;
    DateTime _dwellSince;
    Win32.POINT _anchor;
    IDropTargetHelper? _dragImage;
    CancellationTokenSource? _cts;

    bool _revealed;
    readonly DispatcherTimer _probeTimer;

    public bool IsBusy => _busy;
    public bool IsShowing => _visible;

    /// <summary>Test hook: the ring's collapse value, to catch a stale animation.</summary>
    public double CollapseForTest => _menu.Collapse;

    /// <summary>Test hook: the ring's diameter in device-independent pixels.</summary>
    public double RingSizeForTest => _menu.Geometry.Size;

    /// <summary>Test hook: runs a job the way a drop does, without a live drag.</summary>
    public void RunJobForTest(string actionId, DragPayload payload) => _ = RunJobAsync(actionId, payload);

    /// <summary>Raised when a summon turned out not to be a file drag (no OLE data arrived).</summary>
    public event Action? Abandoned;

    public OverlayWindow(IOverlayHost host)
    {
        _host = host;
        Title = "DragDone";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = _canvasW;
        Height = _canvasH;
        AllowDrop = true;
        FontFamily = Theme.UiFont;
        Opacity = 0;
        Left = -10000;
        Top = -10000;

        BuildVisuals();

        _dwellTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _dwellTimer.Tick += (_, _) => CheckDwell();
        _spinTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _spinTimer.Tick += (_, _) => _menu.Spin = (_menu.Spin + 6) % 360;
        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _probeTimer.Tick += (_, _) =>
        {
            _probeTimer.Stop();
            if (_revealed || _busy) return;
            Diag.Log("probe timeout: no OLE file data arrived, abandoning");
            HideMenu();
            Abandoned?.Invoke();
        };
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideTimer.Tick += (_, _) =>
        {
            if (_chip.IsMouseOver) return; // reading or about to click
            HideMenu();
        };

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        SourceInitialized += (_, _) => MakeNonActivating();
        DpiChanged += (_, _) =>
        {
            if (_visible) Place(_anchor);
        };
        Loc.LanguageChanged += OnLanguageChanged;
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            Theme.Changed -= OnThemeChanged;
        };
    }

    void OnThemeChanged()
    {
        RefreshTabs();
        Layout();
    }

    void OnLanguageChanged()
    {
        FlowDirection = Loc.Flow;
        _infoRow.FlowDirection = Loc.Flow;
        if (_payload is not null)
        {
            _infoTitle.Text = Loc.KindName(_payload.Kind, _payload.Count, _payload.Office);
            _infoMeta.Text = _payload.Count > 1
                ? $"{_payload.Count} · {Loc.Size(_payload.TotalBytes)}"
                : Loc.Size(_payload.TotalBytes);
        }
        Layout();
    }

    // ------------------------------------------------------------------ visuals

    /// <summary>
    /// Sizes the window to the configured ring, snaps the ring to its slot, and reports the
    /// panel height so the window can grow with it. Called on every appearance change.
    /// </summary>
    void Layout()
    {
        _menu.Look = _host.Settings;
        _canvasW = _menu.Geometry.Size;

        _root.Width = _canvasW;
        _menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _below.Width = _canvasW;
        _below.Measure(new Size(_canvasW, double.PositiveInfinity));
        var belowHeight = _below.DesiredSize.Height;
        _canvasH = MenuTop + _menu.Geometry.Size + (belowHeight > 0 ? BelowGap + belowHeight : 0);
        // The window itself must match the canvas. It was only ever sized from the constructor's
        // placeholder numbers, which clipped the key bar and caption and pushed the ring off-centre.
        Width = _canvasW;
        Height = _canvasH;
        _root.Height = _canvasH;
        _root.RenderTransformOrigin = new Point(HubCenterInWindow.X / _canvasW, HubCenterInWindow.Y / _canvasH);
        Canvas.SetLeft(_menu, MenuLeft);
        Canvas.SetTop(_menu, MenuTop);
        Canvas.SetLeft(_below, 0);
        Canvas.SetTop(_below, MenuTop + _menu.Geometry.Size + BelowGap);
        Canvas.SetTop(_chip, HubCenterInWindow.Y + _menu.Geometry.HubRadius + 14);
        Canvas.SetTop(_infoPill, 4);
        _tabsBar.Visibility = _host.Settings.ShowTabBar ? Visibility.Visible : Visibility.Collapsed;
        if (_visible) Place(_anchor);
    }

    void BuildVisuals()
    {
        FlowDirection = Loc.Flow;
        Content = _root;
        _root.RenderTransform = _scale;
        _menu.Look = _host.Settings;

        Canvas.SetLeft(_menu, MenuLeft);
        Canvas.SetTop(_menu, MenuTop);
        _root.Children.Add(_menu);

        _tabsBar.CornerRadius = new CornerRadius(19);
        _tabsBar.Background = Theme.Brush(Theme.Current.SurfaceAlt);
        _tabsBar.BorderBrush = Theme.Brush(Theme.Current.Stroke);
        _tabsBar.BorderThickness = new Thickness(1);
        _tabsBar.Padding = new Thickness(3);
        _tabsBar.Child = _tabs;
        foreach (var tab in ActionCatalog.TabOrder)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12, 4, 12, 5),
                Margin = new Thickness(1, 0, 1, 0),
                Tag = tab,
            };
            _pills[tab] = pill;
            _tabs.Children.Add(pill);
        }
        _tabsBar.HorizontalAlignment = HorizontalAlignment.Center;
        _below.Children.Add(_tabsBar);

        _caption.Foreground = Theme.Brush(Theme.Current.TextSoft);
        _caption.FontSize = 11.5;
        _caption.TextAlignment = TextAlignment.Center;
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.MaxWidth = 300;
        _captionBox.Child = _caption;
        _captionBox.Background = Theme.Brush(Theme.Current.SurfaceAlt);
        _captionBox.CornerRadius = new CornerRadius(8);
        _captionBox.Padding = new Thickness(10, 3, 10, 4);
        _captionBox.HorizontalAlignment = HorizontalAlignment.Center;
        _captionBox.Margin = new Thickness(0, 6, 0, 0);
        _below.Children.Add(_captionBox);
        Canvas.SetLeft(_below, 0);
        _root.Children.Add(_below);

        _infoTitle.Foreground = Theme.Brush(Theme.Current.Text);
        _infoTitle.FontSize = 12.5;
        _infoTitle.FontWeight = FontWeights.SemiBold;
        _infoTitle.TextAlignment = TextAlignment.Center;
        _infoTitle.VerticalAlignment = VerticalAlignment.Center;
        _infoTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _infoMeta.Foreground = Theme.Brush(Theme.Current.TextSoft);
        _infoMeta.FontSize = 11;
        _infoMeta.TextAlignment = TextAlignment.Center;
        _infoMeta.VerticalAlignment = VerticalAlignment.Center;
        _infoMeta.Margin = new Thickness(6, 0, 0, 0);
        _infoRow.Children.Add(_infoTitle);
        _infoRow.Children.Add(_infoMeta);
        _infoPill.Child = _infoRow;
        _infoPill.Background = Theme.Brush(Theme.Current.SurfaceAlt);
        _infoPill.BorderBrush = Theme.Brush(Theme.Current.Stroke);
        _infoPill.BorderThickness = new Thickness(1);
        _infoPill.CornerRadius = new CornerRadius(15);
        _infoPill.Padding = new Thickness(12, 3, 12, 4);
        _infoPill.HorizontalAlignment = HorizontalAlignment.Center;
        _infoPill.Visibility = Visibility.Collapsed;
        _root.Children.Add(_infoPill);
        _chip.Visibility = Visibility.Collapsed;
        _root.Children.Add(_chip);
        RefreshTabs();
        Layout(); // the panel exists now, so the canvas can measure it
    }

    void RefreshTabs()
    {
        var caps = _payload is null ? null : _host.Capabilities();
        var settings = _host.Settings;
        _tabsBar.Visibility = settings.ShowTabBar ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (tab, pill) in _pills)
        {
            var active = tab == _tab;
            var empty = _payload is not null && caps is not null && ActionCatalog.Build(_payload, tab, caps).Count == 0;
            var bound = BoundKeys.TabKey(settings, tab);
            pill.Background = active ? Theme.Brush(Theme.Current.Accent) : Brushes.Transparent;
            Brush fg = active ? Theme.Brush(Theme.On(Theme.Current.Accent)) : Theme.Brush(Theme.Current.Text);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = Loc.T("tab." + tab.ToString().ToLowerInvariant()),
                Foreground = fg,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
            });
            row.Children.Add(new Border
            {
                Margin = new Thickness(6, 1, 0, 0),
                Padding = new Thickness(4, 0, 4, 1),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = active ? Theme.Brush(Theme.On(Theme.Current.Accent)) : Theme.Brush(Theme.Current.TextDim),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Keys.Label(bound),
                    FontSize = 9.5,
                    Foreground = fg,
                    FlowDirection = FlowDirection.LeftToRight,
                },
            });
            pill.Child = row;
            pill.Opacity = empty ? 0.4 : 1;
        }
    }

    void SetCaption(string text, bool warn = false)
    {
        _caption.Text = text;
        _caption.Foreground = warn ? Theme.Brush(Theme.Current.Danger) : Theme.Brush(Theme.Current.TextSoft);
    }

    void DefaultCaption() => SetCaption(_ring.Count == 0 && _payload is not null ? Loc.T("caption.emptyTab") : Loc.T("caption.default"));

    // ------------------------------------------------------------------ show / hide

    void MakeNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Creates the HWND off-screen so the first real show is instant.</summary>
    public void Prepare()
    {
        Show();
        Hide();
    }

    /// <summary>Shows the ring centred on the cursor (physical pixels).</summary>
    public void ShowAt(Win32.POINT cursor, MenuTab tab, DragPayload? knownPayload = null)
    {
        _hideTimer.Stop();
        _cts?.Cancel();
        _anchor = cursor;
        _dropped = false;
        _busy = false;
        _path.Clear();
        _payload = knownPayload;
        _tab = tab;
        _lastMods = 0;
        _chip.Visibility = Visibility.Collapsed;
        _tabsBar.Visibility = Visibility.Visible;
        _captionBox.Visibility = Visibility.Visible;
        _menu.Mode = HubMode.Menu;
        _menu.BackLabel = null;
        _menu.HubHover = false;
        // A finished job leaves Collapse animated to 1 (HoldEnd). Clearing the animation is the only
        // way to make the local value win again, otherwise the ring stays invisible for the rest of
        // the session and only the hub is drawn.
        _menu.BeginAnimation(RadialMenu.CollapseProperty, null);
        _menu.Collapse = 0;
        _menu.Spin = 0;
        FlowDirection = Loc.Flow;
        if (_payload is null)
        {
            // Files are not known until OLE delivers DragEnter (a few ms after the nudge).
            _ring = Array.Empty<ActionDef>();
            _menu.Items = _ring;
            _menu.SetHub("…", "", 0);
            _infoPill.Visibility = Visibility.Collapsed;
            SetCaption(Loc.T("caption.default"));
        }
        else
        {
            ApplyPayload(_payload, tab);
        }
        RefreshTabs();

        if (!IsVisible) Show();
        Place(cursor);
        _visible = true;
        _dwellTimer.Start();

        // Until OLE confirms a file drag the window is nearly invisible but still hit-testable
        // (layered windows ignore pixels with zero alpha), so a rubber-band selection never flashes a menu.
        _revealed = false;
        if (knownPayload is not null)
        {
            Reveal();
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0.02;
            _probeTimer.Stop();
            _probeTimer.Start();
        }

        // Make OLE re-hit-test now that a drop target sits under the cursor.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Win32.GetCursorPos(out var p))
            {
                Win32.SetCursorPos(p.X + 1, p.Y);
                Win32.SetCursorPos(p.X, p.Y);
            }
        });
    }

    void Reveal()
    {
        if (_revealed) return;
        _revealed = true;
        _probeTimer.Stop();
        // The ring plays the user's chosen opening animation itself; the window around it (key
        // bar, caption, info pill) only fades, so the two never fight over the same motion.
        var fadeMs = _menu.MotionEnabled ? 110 : 0;
        if (fadeMs > 0) BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Min(Opacity, 0.02), 1, TimeSpan.FromMilliseconds(fadeMs)));
        else
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
        _menu.PlayOpen();
    }

    void Place(Win32.POINT cursor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTONEAREST);
        var scale = 1.0;
        if (Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var wPx = (int)Math.Round(_canvasW * scale);
        var hPx = (int)Math.Round(_canvasH * scale);
        var x = cursor.X - (int)Math.Round(HubCenterInWindow.X * scale);
        var y = cursor.Y - (int)Math.Round(HubCenterInWindow.Y * scale);
        x = Math.Clamp(x, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - wPx));
        y = Math.Clamp(y, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - hPx));
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        if (Diag.Enabled)
        {
            Diag.Log($"place: cursor={cursor.X},{cursor.Y} monitorScale={scale:0.00} windowDpi={VisualTreeHelper.GetDpi(this).DpiScaleX:0.00} " +
                     $"canvas={_canvasW:0}x{_canvasH:0}dip hub=({HubCenterInWindow.X:0},{HubCenterInWindow.Y:0})dip -> window={x},{y} {wPx}x{hPx}px work=[{info.rcWork.Left},{info.rcWork.Top},{info.rcWork.Right},{info.rcWork.Bottom}]");
        }
    }

    public void HideMenu()
    {
        _hideTimer.Stop();
        _dwellTimer.Stop();
        _spinTimer.Stop();
        _probeTimer.Stop();
        _visible = false;
        _busy = false;
        _payload = null;
        _infoPill.Visibility = Visibility.Collapsed;
        try
        {
            _dragImage?.Show(false);
        }
        catch (COMException) { }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            if (!_visible) Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The mouse button went up somewhere. If nothing was dropped on us, go away.</summary>
    public void OnMouseReleased()
    {
        if (!_visible || _busy || _dropped) return;
        // OLE delivers Drop just after the button-up the hook sees; give it a moment.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!_dropped && !_busy && _visible) HideMenu();
        };
        t.Start();
    }

    // ------------------------------------------------------------------ payload / rings

    void ApplyPayload(DragPayload payload, MenuTab wanted)
    {
        _payload = payload;
        var caps = _host.Capabilities();
        _tab = ActionCatalog.Resolve(payload, wanted, caps);
        _path.Clear();
        _menu.BackLabel = null;
        _ring = ActionCatalog.Build(payload, _tab, caps);
        _menu.Items = _ring;
        _menu.SetHub(Loc.KindName(payload.Kind, payload.Count, payload.Office), Loc.Size(payload.TotalBytes), payload.Count);
        // The hub sits exactly under the cursor, where Explorer's drag ghost always covers it.
        // The file kind, size and count are therefore shown in a pill above the ring instead.
        _infoTitle.Text = Loc.KindName(payload.Kind, payload.Count, payload.Office);
        _infoMeta.Text = payload.Count > 1
            ? $"{payload.Count} · {Loc.Size(payload.TotalBytes)}"
            : Loc.Size(payload.TotalBytes);
        _infoPill.Visibility = Visibility.Visible;
        _infoPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var pillWidth = _infoPill.DesiredSize.Width;
        _infoTitle.MaxWidth = Math.Max(60, _canvasW - 150);
        _infoMeta.MaxWidth = Math.Max(40, _canvasW - 150);
        // Inside a Canvas, HorizontalAlignment is ignored: the pill must be positioned by hand.
        Canvas.SetLeft(_infoPill, Math.Max(0, (_canvasW - pillWidth) / 2));
        Canvas.SetTop(_infoPill, 4);
        if (Diag.Enabled) Diag.Log($"payload applied: wanted={wanted} resolved={_tab} items={_ring.Count} [{string.Join(",", _ring.Select(r => r.Id))}] revealed={_revealed}");
        ReportSectors();
        RefreshTabs();
        DefaultCaption();
    }

    /// <summary>Reports where each sector sits on screen, so a test harness can drop on one exactly.</summary>
    void ReportSectors()
    {
        if (!Diag.Enabled || _ring.Count == 0) return;
        var g = _menu.Geometry;
        var mid = (g.InnerRadius + g.OuterRadius) / 2;
        var monitor = Win32.MonitorFromPoint(_anchor, Win32.MONITOR_DEFAULTTONEAREST);
        var scale = 1.0;
        if (Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;
        var origin = OriginOnScreen(scale);
        for (var i = 0; i < _ring.Count; i++)
        {
            var a = _menu.Geometry.Mid(i);
            var p = RadialGeometry.PointAt(g.Center, mid, a);
            var sx = origin.X + (int)Math.Round((MenuLeft + p.X) * scale);
            var sy = origin.Y + (int)Math.Round((MenuTop + p.Y) * scale);
            Diag.Log($"sector {i} {_ring[i].Id} at {sx},{sy}");
        }
    }

    /// <summary>The window's top-left corner on the screen, in physical pixels.</summary>
    Point OriginOnScreen(double scale)
    {
        var x = _anchor.X - (int)Math.Round(HubCenterInWindow.X * scale);
        var y = _anchor.Y - (int)Math.Round(HubCenterInWindow.Y * scale);
        var monitor = Win32.MonitorFromPoint(_anchor, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(monitor, ref info);
        var wPx = (int)Math.Round(_canvasW * scale);
        var hPx = (int)Math.Round(_canvasH * scale);
        x = Math.Clamp(x, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - wPx));
        y = Math.Clamp(y, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - hPx));
        return new Point(x, y);
    }

    void SwitchTab(MenuTab tab)
    {
        if (_payload is null || tab == _tab && _path.Count == 0) return;
        _tab = tab;
        _path.Clear();
        _menu.BackLabel = null;
        _ring = ActionCatalog.Build(_payload, tab, _host.Capabilities());
        _menu.Items = _ring;
        _menu.PlayOpen(transition: true);
        RefreshTabs();
        DefaultCaption();
    }

    void OpenSubmenu(ActionDef parent)
    {
        _path.Push(parent);
        _ring = parent.Children;
        _menu.Items = _ring;
        _menu.BackLabel = parent.Label;
        _menu.PlayOpen(transition: true);
        DefaultCaption();
    }

    void CloseSubmenu()
    {
        if (_path.Count == 0 || _payload is null) return;
        _path.Pop();
        if (_path.Count > 0)
        {
            _ring = _path.Peek().Children;
            _menu.BackLabel = _path.Peek().Label;
        }
        else
        {
            _ring = ActionCatalog.Build(_payload, _tab, _host.Capabilities());
            _menu.BackLabel = null;
        }
        _menu.Items = _ring;
        _menu.PlayOpen(transition: true);
        DefaultCaption();
    }

    // ------------------------------------------------------------------ OLE drag & drop

    static string[]? FilesOf(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    void OnDragEnter(object sender, DragEventArgs e)
    {
        Diag.Log($"OLE DragEnter: visible={_visible} busy={_busy} formats=[{string.Join(",", e.Data.GetFormats())}]");
        if (_busy || !_visible)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        var files = FilesOf(e);
        if (files is { Length: > 0 })
        {
            if (_payload is null || !_payload.Paths.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
                ApplyPayload(new DragPayload(files), _tab);
            Reveal();
        }
        else if (!_revealed)
        {
            // Text, links, or a virtual item (a phone, a zip entry): not ours.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            HideMenu();
            Abandoned?.Invoke();
            return;
        }
        DragImageEnter(e);
        UpdateFromDrag(e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (_busy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        UpdateFromDrag(e);
        try
        {
            var p = CursorPoint();
            _dragImage?.DragOver(ref p, (int)e.Effects);
        }
        catch (COMException) { }
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        _menu.HoverIndex = -1;
        _menu.HubHover = false;
        _dwellTarget = null;
        try
        {
            _dragImage?.DragLeave();
        }
        catch (COMException) { }
        if (!_busy && _visible) DefaultCaption();
    }

    void UpdateFromDrag(DragEventArgs e)
    {
        // Modifier changes switch the tab live (edge-triggered so holding keys is stable).
        var mods = e.KeyStates & (DragDropKeyStates.ShiftKey | DragDropKeyStates.ControlKey | DragDropKeyStates.AltKey);
        var pressed = mods & ~_lastMods;
        _lastMods = mods;
        if ((pressed & DragDropKeyStates.ShiftKey) != 0) SwitchTab(MenuTab.Convert);
        else if ((pressed & DragDropKeyStates.ControlKey) != 0) SwitchTab(MenuTab.Tools);
        else if ((pressed & DragDropKeyStates.AltKey) != 0) SwitchTab(MenuTab.Actions);

        var effect = DragDropEffects.None;
        object? target = null;

        foreach (var (tab, pill) in _pills)
        {
            var pp = e.GetPosition(pill);
        }

        var (zone, index) = _menu.HitTestRing(e.GetPosition(_menu));
        _menu.HubHover = zone == RadialZone.Hub;
        if (target is null && zone == RadialZone.Ring && index >= 0 && index < _ring.Count)
        {
            var item = _ring[index];
            _menu.HoverIndex = index;
            target = item;
            if (item.Enabled && (!item.HasChildren || item.DefaultChildId is not null)) effect = DragDropEffects.Copy;
            if (!item.Enabled) SetCaption(Loc.T(item.DisabledReasonKey!), warn: true);
            else if (item.HintKey is not null) SetCaption(Loc.T(item.HintKey));
            else SetCaption(item.HasChildren ? Loc.F("caption.submenu", ("name", item.Label)) : item.Label);
        }
        else
        {
            _menu.HoverIndex = -1;
            if (zone == RadialZone.Hub && _path.Count > 0)
            {
                target = "hub";
                SetCaption(Loc.T("caption.back"));
            }
            else if (zone == RadialZone.Hub && _payload is not null)
            {
                SetCaption(Loc.T("caption.cancel"));
            }
            else if (target is null && _payload is not null)
            {
                DefaultCaption();
            }
        }

        if (!ReferenceEquals(target, _dwellTarget) && !Equals(target, _dwellTarget))
        {
            _dwellTarget = target;
            _dwellSince = DateTime.UtcNow;
        }

        e.Effects = effect;
        e.Handled = true;
    }

    void CheckDwell()
    {
        if (!_visible || _busy || _dwellTarget is null) return;
        var held = DateTime.UtcNow - _dwellSince;
        switch (_dwellTarget)
        {
            case MenuTab tab when held.TotalMilliseconds > 220:
                SwitchTab(tab);
                _dwellTarget = null;
                break;
            case ActionDef { HasChildren: true, Enabled: true } parent when held.TotalMilliseconds > 380:
                OpenSubmenu(parent);
                _dwellTarget = null;
                break;
            case "hub" when held.TotalMilliseconds > 380:
                CloseSubmenu();
                _dwellTarget = null;
                break;
        }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var p = CursorPoint();
            _dragImage?.Drop(ComData(e)!, ref p, (int)DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException) { }

        var files = FilesOf(e);
        var (zone, index) = _menu.HitTestRing(e.GetPosition(_menu));
        if (_busy || files is not { Length: > 0 } || zone != RadialZone.Ring || index < 0 || index >= _ring.Count)
        {
            e.Effects = DragDropEffects.None;
            HideMenu();
            return;
        }

        var item = _ring[index];
        string? id = item.Enabled ? item.HasChildren ? item.DefaultChildId : item.Id : null;
        if (id is null)
        {
            e.Effects = DragDropEffects.None;
            HideMenu();
            return;
        }

        _dropped = true;
        e.Effects = DragDropEffects.Copy;
        var payload = _payload is not null && _payload.Paths.SequenceEqual(files, StringComparer.OrdinalIgnoreCase)
            ? _payload
            : new DragPayload(files);

        // Return to OLE immediately; Explorer is blocked until Drop returns.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Start(id, payload));
    }

    // ------------------------------------------------------------------ running

    void Start(string id, DragPayload payload)
    {
        if (ActionExecutor.IsInteractive(id))
        {
            HideMenu();
            _host.RunInteractive(id, payload);
            return;
        }
        _ = RunJobAsync(id, payload);
    }

    /// <summary>Runs a job with the progress hub and result chip. Also used after interactive steps.</summary>
    public async Task RunJobAsync(string id, DragPayload payload, Win32.POINT? at = null)
    {
        if (at is { } point)
        {
            ShowAt(point, _tab, payload);
            _dropped = true;
        }
        _busy = true;
        _dwellTimer.Stop();
        _menu.HoverIndex = -1;
        _menu.HubHover = false;
        _menu.BackLabel = null;
        _menu.Mode = HubMode.Busy;
        _menu.Progress = null;
        _tabsBar.Visibility = Visibility.Collapsed;
        _captionBox.Visibility = Visibility.Collapsed;
        _menu.BeginAnimation(RadialMenu.CollapseProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
        if (Diag.Enabled) Diag.Log($"job start: {id}");
        _spinTimer.Start();

        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(v =>
        {
            if (_busy) _menu.Progress = v;
        });
        var ctx = new JobContext(_host.Settings, progress, _cts.Token);
        var result = await ActionExecutor.RunAsync(id, payload, ctx);
        _spinTimer.Stop();
        if (Diag.Enabled) Diag.Log($"job end: {id} outcome={result.Outcome} outputs={result.Outputs.Count}");
        ShowResult(result);
    }

    void ShowResult(JobResult result)
    {
        _busy = false;
        _menu.Progress = null;
        _menu.Mode = result.Outcome switch
        {
            JobOutcome.Created or JobOutcome.Info => HubMode.Done,
            JobOutcome.Failed => HubMode.Failed,
            _ => HubMode.Unchanged,
        };
        if (result.Clipboard is not null)
        {
            try
            {
                Clipboard.SetText(result.Clipboard);
            }
            catch (COMException) { }
        }
        if (result.Undo is { IsEmpty: false } undo) _host.RememberUndo(undo);
        BuildChip(result);
        _hideTimer.Interval = TimeSpan.FromSeconds(result.Outcome == JobOutcome.Failed ? Math.Max(6, _host.Settings.ResultSeconds) : _host.Settings.ResultSeconds);
        _hideTimer.Start();
    }

    void BuildChip(JobResult result)
    {
        var panel = new StackPanel();
        var headline = result.Outcome == JobOutcome.Created && result.Outputs.Count == 1 && result.MessageKey == "result.done"
            ? Loc.F("result.savedAs", ("name", Path.GetFileName(result.Outputs[0])))
            : result.Outcome == JobOutcome.Created && result.Outputs.Count > 1 && result.MessageKey == "result.done"
                ? Loc.F("result.savedMany", ("n", result.Outputs.Count))
                : Loc.T(result.MessageKey);
        panel.Children.Add(new TextBlock
        {
            Text = headline,
            Foreground = Theme.Brush(Theme.Current.Text),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(result.Detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = result.Detail,
                Foreground = Theme.Brush(result.Outcome == JobOutcome.Failed ? Theme.Current.Danger : Theme.Current.TextSoft),
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxHeight = 48,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        if (result.Outcome == JobOutcome.Created && result.Outputs.Count > 0)
        {
            var first = result.Outputs[0];
            buttons.Children.Add(ChipButton(Glyphs.OpenFolder, Loc.T("chip.showInFolder"), () => Reveal(first)));
            if (result.Outputs.Count == 1 && File.Exists(first))
                buttons.Children.Add(ChipButton(Glyphs.Page, Loc.T("chip.open"), () => OpenPath(first)));
            if (result.Undo is { IsEmpty: false } undo)
            {
                buttons.Children.Add(ChipButton(Glyphs.Undo, Loc.T("chip.undo"), () =>
                {
                    var ok = ActionExecutor.Undo(undo);
                    _menu.Mode = ok ? HubMode.Unchanged : HubMode.Failed;
                    BuildChip(new JobResult { Outcome = ok ? JobOutcome.Info : JobOutcome.Failed, MessageKey = ok ? "result.undone" : "result.undoFailed" });
                    _hideTimer.Stop();
                    _hideTimer.Start();
                }));
            }
        }
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);

        _chip.Child = panel;
        _chip.Width = 296;
        _chip.Padding = new Thickness(14, 10, 14, 10);
        _chip.CornerRadius = new CornerRadius(14);
        _chip.Background = Theme.Brush(Theme.Current.Surface);
        _chip.BorderBrush = Theme.Brush(Theme.Current.Stroke);
        _chip.BorderThickness = new Thickness(1);
        _chip.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.4 };
        _chip.Visibility = Visibility.Visible;
        Canvas.SetLeft(_chip, (_canvasW - 296) / 2);
        Canvas.SetTop(_chip, HubCenterInWindow.Y + _menu.Geometry.HubRadius + 12);
        _chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    Border ChipButton(string glyph, string text, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var rest = Theme.Brush(Theme.Current.Text);
        var glyphBlock = new TextBlock { Text = glyph, FontFamily = Theme.IconFont, FontSize = 12, Foreground = rest, VerticalAlignment = VerticalAlignment.Center };
        var textBlock = new TextBlock { Text = text, FontSize = 12, Foreground = rest, Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(glyphBlock);
        row.Children.Add(textBlock);
        var b = new Border
        {
            Child = row,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.Current.Stroke),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.MouseEnter += (_, _) =>
        {
            b.Background = Theme.Brush(Theme.Current.Accent);
            glyphBlock.Foreground = textBlock.Foreground = Theme.Brush(Theme.On(Theme.Current.Accent));
        };
        b.MouseLeave += (_, _) =>
        {
            b.Background = Theme.Brush(Theme.Current.Stroke);
            glyphBlock.Foreground = textBlock.Foreground = rest;
        };
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    public static void Reveal(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // ------------------------------------------------------------------ drag image (shell helper)

    static ComDataObject? ComData(DragEventArgs e) => e.Data as ComDataObject;

    static Win32.POINT CursorPoint()
    {
        Win32.GetCursorPos(out var p);
        return p;
    }

    void DragImageEnter(DragEventArgs e)
    {
        try
        {
            _dragImage ??= (IDropTargetHelper)new DragDropHelper();
            var data = ComData(e);
            if (data is null) return;
            var p = CursorPoint();
            _dragImage.DragEnter(new WindowInteropHelper(this).Handle, data, ref p, (int)DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            _dragImage = null;
        }
    }

    [ComImport]
    [Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    class DragDropHelper;

    [ComImport]
    [Guid("4657278B-411B-11D2-839A-00C04FD918D0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComDataObject dataObject, ref Win32.POINT pt, int effect);
        void DragLeave();
        void DragOver(ref Win32.POINT pt, int effect);
        void Drop(ComDataObject dataObject, ref Win32.POINT pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    // ------------------------------------------------------------------ snapshot (docs / visual tests)

    /// <summary>Renders the overlay in a given state without showing a window.</summary>
    public FrameworkElement SnapshotRoot(DragPayload payload, MenuTab tab, int hover, string? openSubmenuId = null)
    {
        FlowDirection = Loc.Flow;
        _visible = true;
        ApplyPayload(payload, tab);
        if (openSubmenuId is not null)
        {
            var parent = _ring.FirstOrDefault(a => a.Id == openSubmenuId);
            if (parent is not null) OpenSubmenu(parent);
        }
        _menu.HoverIndex = hover;
        if (hover >= 0 && hover < _ring.Count)
        {
            var item = _ring[hover];
            if (!item.Enabled) SetCaption(Loc.T(item.DisabledReasonKey!), true);
            else if (item.HintKey is not null) SetCaption(Loc.T(item.HintKey));
            else SetCaption(item.Label);
        }
        _visible = false;
        Content = null;
        _root.FlowDirection = Loc.Flow;
        return _root;
    }

    public FrameworkElement SnapshotResult(JobResult result)
    {
        FlowDirection = Loc.Flow;
        _tabsBar.Visibility = Visibility.Collapsed;
        _captionBox.Visibility = Visibility.Collapsed;
        _menu.Collapse = 1;
        _menu.Mode = HubMode.Done;
        BuildChip(result);
        _chip.BeginAnimation(OpacityProperty, null);
        Content = null;
        _root.FlowDirection = Loc.Flow;
        return _root;
    }
}
