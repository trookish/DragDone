using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DragDone.Core;
using DragDone.Localization;
using DragDone.Processors;
using DragDone.Shell;
using Forms = System.Windows.Forms;

namespace DragDone.UI;

/// <summary>
/// Settings, built in code so both languages and directions share one layout. Every control
/// writes straight into the settings object, applies the look live, and saves shortly after
/// the user stops moving it.
/// </summary>
public sealed class SettingsWindow : FluentWindow
{
    readonly Settings _s;
    readonly Action _changed;
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 14, 24, 20) };
    readonly TextBox _search = new();
    readonly DispatcherTimer _save;
    readonly DispatcherTimer _preview;
    readonly DispatcherTimer _restyle;

    // The page is thrown away and rebuilt whenever the look changes, so nothing inside it is
    // kept across builds. These two images are: they share the rendered bitmap, never a parent.
    Image _previewInline = new() { Stretch = Stretch.Uniform, Height = 236, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Image _previewFloat = new() { Stretch = Stretch.Uniform, Height = 148 };

    // The floating copy sits above the page, so scrolling it out of view cannot take it away.
    readonly Canvas _floatLayer = new();
    readonly Border _floatCard;
    readonly TranslateTransform _slide = new();
    FrameworkElement? _previewSlot;
    bool _floatShown;
    bool _draggingPreview;
    Point _dragGrab;

    // Each choice row needs its own group name, or one row would uncheck another's pick.
    int _segments;

    // Where the page should stay while it is rebuilt underneath the user, and whether a rebuild
    // is currently in flight (in which case the layout resets it causes are not real scrolls).
    double _lastOffset;
    bool _holdingScroll;

    // Swapped as a whole by each build: the page is only adopted once it is complete, and
    // these have to describe that page or the search filter would hide the wrong rows.
    List<(FrameworkElement Element, string Text)> _searchable = new();
    List<(StackPanel Section, UIElement Body)> _sections = new();

    static SettingsWindow? _instance;

    // Building the controls touches selection and text handlers, which would otherwise look like
    // user edits: they would save on open, and rewrite a hand-edited theme file nobody changed.
    bool _loading;
    bool _dirty;

    public SettingsWindow(Settings settings, Action changed)
    {
        _s = settings;
        _changed = changed;
        Width = 760;
        Height = 880;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        var page = new DockPanel();
        var top = new StackPanel { Margin = new Thickness(24, 16, 24, 8) };
        top.Children.Add(Header());
        top.Children.Add(SearchBox());
        DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(top);
        page.Children.Add(_scroll);
        root.Children.Add(page);

        // Above the page, so it stays put while the page scrolls underneath it.
        // The layer stays left-to-right so a corner is where it says it is in every language;
        // the card itself still follows the interface direction for its caption.
        _floatLayer.FlowDirection = FlowDirection.LeftToRight;
        _floatCard = FloatingPreview();
        _floatCard.FlowDirection = Loc.Flow;
        _floatCard.Visibility = Visibility.Collapsed;
        _floatCard.Opacity = 0;
        _floatLayer.Children.Add(_floatCard);
        root.Children.Add(_floatLayer);
        Content = root;

        _save = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _save.Stop();
        _preview = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => DrawPreview(), Dispatcher);
        _preview.Stop();
        _restyle = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) => Restyle(), Dispatcher);
        _restyle.Stop();

        _scroll.ScrollChanged += (_, _) =>
        {
            if (!_holdingScroll) _lastOffset = _scroll.VerticalOffset;
            SyncFloatingPreview();
        };
        SizeChanged += (_, _) => { if (_floatShown) PlaceFloating(); };

        RunLoading(Build);
        Loaded += (_, _) =>
        {
            DrawPreview();
            SyncFloatingPreview();
        };
        Closed += (_, _) =>
        {
            Flush();
            _instance = null;
        };
        _instance = this;
    }

    protected override void OnThemeChanging()
    {
        // The chrome re-template wipes the scroll position before OnPaletteChanged runs, so park
        // it here and stop recording the resets until the page has been rebuilt.
        _lastOffset = _scroll.VerticalOffset;
        _holdingScroll = true;
    }

    protected override void OnPaletteChanged()
    {
        _preview.Stop();
        _preview.Start();
        // Restyling means rebuilding the page, which replaces whichever control the user is
        // holding. Wait for them to let go instead of yanking it out from under the pointer.
        _restyle.Stop();
        _restyle.Start();
    }

    /// <summary>Rebuilds the page to pick up a new palette, unless a control is being dragged.</summary>
    void Restyle()
    {
        _restyle.Stop();
        if (Mouse.Captured is not null)
        {
            _restyle.Start();
            return;
        }
        Rebuild();
    }

    /// <summary>Reuses the open window instead of stacking a second one.</summary>
    public static void Show(Settings settings, Action changed)
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }
        new SettingsWindow(settings, changed).Show();
    }

    protected override void OnLanguageChanged()
    {
        FlowDirection = Loc.Flow;
        Rebuild();
    }

    void RunLoading(Action build)
    {
        _loading = true;
        try { build(); }
        finally { _loading = false; }
    }

    /// <summary>Saves once the user has stopped fiddling, and pushes the look through at once.</summary>
    void Commit()
    {
        if (_loading) return;
        _dirty = true;
        _changed();
        _preview.Stop();
        _preview.Start();
        _save.Stop();
        _save.Start();
    }

    void Flush()
    {
        _save.Stop();
        if (!_dirty) return;
        _dirty = false;
        _s.Save();
    }

    void Rebuild()
    {
        // Read the parked position, not the live one: a chrome change or an earlier rebuild may
        // have already reset it to the top, and that reset is not where the user was.
        var offset = _lastOffset;
        RunLoading(Build);
        // The new page starts with everything visible, so a typed search has to be put back.
        if (_search.Text.Length > 0) Filter(_search.Text);
        RestoreScroll(offset);
    }

    /// <summary>
    /// Puts the view back where the user left it. A page that was just assigned — or a window
    /// re-templated by a theme change — reports a zero extent until the next layout pass, so the
    /// jump is repeated once the dispatcher is idle. Scroll tracking stays off in the meantime,
    /// otherwise those resets would be recorded as the user scrolling to the top.
    /// </summary>
    void RestoreScroll(double offset)
    {
        _holdingScroll = true;
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scroll.ScrollToVerticalOffset(offset);
            _lastOffset = _scroll.VerticalOffset;
            _holdingScroll = false;
            SyncFloatingPreview();
        }));
    }

    // ------------------------------------------------------------------ chrome

    UIElement Header()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Image { Source = AppIcon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Heading("DragDone", 22));
        titles.Children.Add(Muted(Loc.T("app.tagline")));
        header.Children.Add(titles);
        return header;
    }

    UIElement SearchBox()
    {
        _search.Text = string.Empty;
        _search.Padding = new Thickness(10, 6, 10, 6);
        _search.FontSize = 13;
        _search.Tag = "search";
        _search.TextChanged += (_, _) => Filter(_search.Text);

        var hint = Muted(Loc.T("settings.search.hint"), 11.5);
        hint.Margin = new Thickness(2, 0, 2, 4);
        return new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 2),
            Children =
            {
                hint,
                new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = Theme.Brush(Theme.Current.Surface),
                    BorderBrush = Theme.Brush(Theme.Current.Stroke),
                    BorderThickness = new Thickness(1),
                    Child = _search,
                },
            },
        };
    }

    void Filter(string query)
    {
        query = query.Trim();
        foreach (var (section, _) in _sections) section.Visibility = Visibility.Collapsed;
        foreach (var (element, text) in _searchable)
            element.Visibility = query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        // A section stays only when something inside it survived the filter.
        foreach (var (section, body) in _sections)
        {
            if (query.Length == 0) section.Visibility = Visibility.Visible;
            else section.Visibility = ContainsVisible(body) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Applies the search filter, so a screenshot can show one section on its own.</summary>
    public void ApplyFilter(string query) => Filter(query);

    /// <summary>How far the page is scrolled. Used by the self-test and by scrolled captures.</summary>
    internal double ScrollOffset => _scroll.VerticalOffset;

    /// <summary>Whether the preview is currently floating over the page.</summary>
    internal bool FloatingPreviewShown => _floatShown;

    /// <summary>Moves the page and settles the floating preview, for captures and the self-test.</summary>
    internal void ScrollToY(double offset)
    {
        _scroll.ScrollToVerticalOffset(offset);
        _scroll.UpdateLayout();
        _lastOffset = _scroll.VerticalOffset;
        SyncFloatingPreview();
    }

    static bool ContainsVisible(UIElement element) => element switch
    {
        Panel panel => panel.Visibility == Visibility.Visible && panel.Children.Cast<UIElement>().Any(ContainsVisible),
        FrameworkElement fe => fe.Visibility == Visibility.Visible,
        _ => false,
    };

    void Build()
    {
        Title = Loc.T("settings.title");

        // Assemble the page on the side and adopt it only once every section is built. Clearing
        // first and filling in afterwards meant one control that threw left the window with no
        // content at all, which is exactly what a failed rebuild used to look like.
        var page = new StackPanel();
        var searchable = _searchable;
        var sections = _sections;
        _searchable = new List<(FrameworkElement Element, string Text)>();
        _sections = new List<(StackPanel Section, UIElement Body)>();
        try
        {
            page.Children.Add(Section("settings.section.appearance", "settings.section.appearance.info", Appearance()));
            page.Children.Add(Section("settings.section.ring", "settings.section.ring.info", Ring()));
            page.Children.Add(Section("settings.section.shortcuts", "settings.section.shortcuts.info", Shortcuts()));
            page.Children.Add(Section("settings.section.general", "settings.section.general.info", General()));
            page.Children.Add(Section("settings.section.output", "settings.section.output.info", Output()));
            page.Children.Add(Section("settings.section.tools", "settings.section.tools.info", Tools()));
            page.Children.Add(Section("settings.section.themes", "settings.section.themes.info", Themes()));
            page.Children.Add(Section("settings.section.about", "settings.section.about.info", About()));
            _scroll.Content = page;
        }
        catch
        {
            // Keep the page that is already on screen, so a broken section is a missing update
            // rather than an empty window.
            _searchable = searchable;
            _sections = sections;
            throw;
        }
    }

    UIElement Section(string titleKey, string infoKey, UIElement body)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        head.Children.Add(new TextBlock
        {
            Text = Loc.T(titleKey),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.Current.Text),
        });
        head.Children.Add(InfoDot(Loc.T(infoKey)));
        panel.Children.Add(head);
        panel.Children.Add(body);
        _sections.Add((panel, body));
        return panel;
    }

    /// <summary>One settings row: title and description, with the control on the end side.</summary>
    UIElement Row(string titleKey, string? descKey, FrameworkElement control, string? infoKey = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = Loc.T(titleKey), FontSize = 13.5, Foreground = Theme.Brush(Theme.Current.Text) });
        if (infoKey is not null) titleRow.Children.Add(InfoDot(Loc.T(infoKey)));
        text.Children.Add(titleRow);
        if (descKey is not null) text.Children.Add(Muted(Loc.T(descKey)));
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        _searchable.Add((grid, Loc.T(titleKey) + " " + (descKey is null ? "" : Loc.T(descKey)) + " " + titleKey));
        return grid;
    }

    Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) stack.Children.Add(c);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(Theme.Current.Surface),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 12),
            Child = stack,
        };
    }

    static Separator Divider() => new() { Margin = new Thickness(0, 6, 0, 6), Opacity = 0.35 };

    ToggleButton Toggle(bool value, Action<bool> set)
    {
        var t = new CheckBox { IsChecked = value, MinWidth = 0 };
        t.Click += (_, _) =>
        {
            set(t.IsChecked == true);
            Commit();
        };
        return t;
    }

    FrameworkElement Choose<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set) where T : notnull
    {
        var list = options.ToList();
        // A few options read better as a row of choices than as a dropdown that has to be opened.
        return list.Count <= 4 ? Segmented(list, current, set) : OptionList(list, current, set);
    }

    /// <summary>A row of pills, one per option. Picking one is a single click and a single value.</summary>
    FrameworkElement Segmented<T>(List<(T Value, string Label)> options, T current, Action<T> set) where T : notnull
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        var style = (Style)FindResource("Segment");
        var group = "choice" + ++_segments;
        foreach (var (value, label) in options)
        {
            var button = new RadioButton
            {
                Content = label,
                GroupName = group,
                Style = style,
                Tag = value,
                IsChecked = Equals(value, current),
            };
            button.Checked += (_, _) =>
            {
                if (_loading) return;
                set(value);
                PaintSegments(strip);
                Commit();
            };
            strip.Children.Add(button);
        }
        PaintSegments(strip);
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.Current.SurfaceAlt),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Child = strip,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Repaints a choice row from its own checked state, so it updates without a rebuild.</summary>
    static void PaintSegments(Panel strip)
    {
        foreach (var button in strip.Children.OfType<RadioButton>())
        {
            var on = button.IsChecked == true;
            button.Background = on ? Theme.Accent : Brushes.Transparent;
            button.Foreground = on ? Theme.OnAccent : Theme.Brush(Theme.Current.TextDim);
        }
    }

    ComboBox OptionList<T>(List<(T Value, string Label)> options, T current, Action<T> set) where T : notnull
    {
        var box = new ComboBox { MinWidth = 168 };
        foreach (var (value, label) in options)
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        var list = box.Items.Cast<ComboBoxItem>().ToList();
        box.SelectedItem = list.FirstOrDefault(i => Equals(i.Tag, current)) ?? list.FirstOrDefault();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem item && item.Tag is T value && !Equals(value, current))
            {
                set(value);
                Commit();
            }
        };
        return box;
    }

    FrameworkElement Choose<T>(IEnumerable<T> values, T current, Action<T> set, Func<T, string> label) where T : notnull =>
        Choose(values.Select(v => (v, label(v))), current, set);

    UIElement Slider(string titleKey, string descKey, double min, double max, double step, double value, Action<double> set, Func<double, string> format, string unit = "")
    {
        var label = new TextBlock { Text = format(value), MinWidth = 76, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush(Theme.Current.Text) };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 170,
            IsSnapToTickEnabled = step > 0,
            TickFrequency = step,
            // Clicking anywhere on the track jumps straight to that value instead of stepping
            // by a page, which is what makes a slider feel direct rather than fiddly.
            IsMoveToPointEnabled = true,
            FlowDirection = FlowDirection.LeftToRight,
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            set(e.NewValue);
            Commit();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return Row(titleKey, descKey, panel);
    }

    UIElement TextRow(string titleKey, string? descKey, string value, Action<string> set, bool ltr = true)
    {
        var box = new TextBox { MinWidth = 240, Text = value, Padding = new Thickness(8, 5, 8, 5), FontSize = 12.5 };
        if (ltr) box.FlowDirection = FlowDirection.LeftToRight;
        box.LostFocus += (_, _) =>
        {
            set(box.Text);
            Commit();
            box.Text = value;
        };
        return Row(titleKey, descKey, box);
    }

    // ------------------------------------------------------------------ appearance

    UIElement Appearance()
    {
        var strip = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var id in Theme.PresetIds)
            strip.Children.Add(PresetTile(id));

        return Card(
            LivePreview(),
            Divider(),
            new TextBlock { Text = Loc.T("settings.theme.preset"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2), Foreground = Theme.Brush(Theme.Current.Text) },
            Muted(Loc.T("settings.theme.preset.desc"), 12),
            strip,
            Divider(),
            Row("settings.theme.mode", "settings.theme.mode.desc", Choose(
                new[] { ColorMode.System, ColorMode.Light, ColorMode.Dark }, _s.Theme,
                v => _s.Theme = v, v => Loc.T("mode." + v.ToString().ToLowerInvariant()))),
            Divider(),
            AccentRow(),
            Slider("settings.accentStrength", "settings.accentStrength.desc", 0.2, 1.6, 0.05, _s.AccentStrength, v => _s.AccentStrength = v,
                v => Loc.Digits(Math.Round(v * 100).ToString("0")) + "%"),
            Slider("settings.contrast", "settings.contrast.desc", 0.6, 1.6, 0.05, _s.Contrast, v => _s.Contrast = v,
                v => Loc.Digits(Math.Round(v * 100).ToString("0")) + "%"),
            Divider(),
            Row("settings.translucent", "settings.translucent.desc", Toggle(_s.Translucent, v => _s.Translucent = v)),
            Row("settings.blur", "settings.blur.desc", Toggle(_s.BackdropBlur, v => _s.BackdropBlur = v)));
    }

    UIElement PresetTile(string id)
    {
        var dark = _s.Theme == ColorMode.Dark || (_s.Theme == ColorMode.System && Theme.SystemUsesDark());
        var probe = new Settings { ThemePreset = id, Theme = dark ? ColorMode.Dark : ColorMode.Light, Accent = _s.Accent, AccentHex = _s.AccentHex };
        var palette = Theme.PaletteFor(probe, dark);
        var selected = _s.ThemePreset == id;

        var bars = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var color in new[] { palette.WheelFill, palette.WheelFillAlt, palette.Accent, palette.Text })
            bars.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                Background = Theme.Brush(color),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1, 0, 1, 0),
            });

        var tile = new Border
        {
            Width = 132,
            Margin = new Thickness(0, 4, 8, 6),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(palette.Surface),
            BorderBrush = Theme.Brush(selected ? palette.Accent : Theme.Current.Stroke),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("theme." + id + ".desc"),
            Child = new StackPanel
            {
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                        Children =
                        {
                            new TextBlock { Text = Loc.T("theme." + id), Foreground = Theme.Brush(palette.Text), FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                            Check(selected, palette.Accent),
                        },
                    },
                    new TextBlock { Text = Loc.T("theme." + id + ".desc"), Foreground = Theme.Brush(palette.TextSoft), FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MinHeight = 26, Margin = new Thickness(0, 2, 0, 4) },
                    bars,
                },
            },
        };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _s.ThemePreset = id;
            Commit();
            Rebuild();
        };
        _searchable.Add((tile, Loc.T("theme." + id) + " " + Loc.T("theme." + id + ".desc") + " theme." + id));
        return tile;
    }

    /// <summary>A tick beside the chosen preset: the border alone would carry the meaning by colour.</summary>
    static UIElement Check(bool on, System.Windows.Media.Color accent)
    {
        var mark = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = Theme.IconFont,
            FontSize = 11,
            Foreground = Theme.Brush(Theme.On(accent)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(accent),
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Child = mark,
            Visibility = on ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(pill, 1);
        return pill;
    }

    UIElement AccentRow()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (id, _) in Theme.Accents)
        {
            var selected = !string.IsNullOrEmpty(_s.AccentHex) ? false : _s.Accent == id;
            var dot = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Theme.AccentFor(id)),
                BorderBrush = selected ? Theme.Brush(Theme.Current.Text) : Theme.Brush(Theme.Current.Stroke),
                BorderThickness = new Thickness(selected ? 3 : 1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("accent." + id),
            };
            if (id == "system")
                dot.Child = new TextBlock
                {
                    Text = "\uE790",
                    FontFamily = Theme.IconFont,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Theme.On(Theme.AccentFor(id))),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                _s.Accent = id;
                _s.AccentHex = null;
                Commit();
                Rebuild();
            };
            wrap.Children.Add(dot);
        }

        var custom = new Button
        {
            Content = Loc.T("settings.accent.custom"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Theme.Brush(Theme.Current.SurfaceAlt),
            Foreground = Theme.Brush(Theme.Current.Text),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        custom.Click += (_, _) => PickCustomAccent();

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(custom);
        if (!string.IsNullOrEmpty(_s.AccentHex))
            right.Children.Add(new TextBlock
            {
                Text = _s.AccentHex,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight,
                Foreground = Theme.Brush(Theme.Current.TextSoft),
                FontSize = 12,
            });

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(wrap);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        _searchable.Add((grid, Loc.T("settings.accent") + " accent"));
        return Row("settings.accent", "settings.accent.desc", grid);
    }

    void PickCustomAccent()
    {
        using var dlg = new Forms.ColorDialog { FullOpen = true, AnyColor = true };
        var current = Theme.AccentFor(_s.Accent);
        dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        _s.AccentHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        Commit();
        Rebuild();
    }

    StackPanel LivePreview()
    {
        // A fresh Image for every build, carrying over whatever was already rendered. The
        // bitmap is shared, the control is not: a control can only have one parent, so reusing
        // this one threw on the second build and took the whole page down with it.
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Height = 236,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = _previewInline.Source,
        };
        _previewInline = image;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(Theme.Current.SurfaceAlt),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = image,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Loc.T("settings.preview"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4), Foreground = Theme.Brush(Theme.Current.Text) });
        stack.Children.Add(frame);
        stack.Children.Add(Muted(Loc.T("settings.preview.hint"), 11.5));
        _searchable.Add((stack, Loc.T("settings.preview") + " preview"));
        // Remembered so the floating copy knows when this one has been scrolled out of sight.
        _previewSlot = stack;
        return stack;
    }

    // ------------------------------------------------------------------ floating preview

    /// <summary>
    /// The copy of the preview that floats over the page once the inline one scrolls away, so a
    /// change can still be judged while editing a row further down. Dragging it picks a corner.
    /// </summary>
    Border FloatingPreview()
    {
        var hint = new TextBlock
        {
            Text = Loc.T("settings.preview.drag"),
            FontSize = 10.5,
            Margin = new Thickness(2, 5, 2, 0),
            Foreground = Theme.Brush(Theme.Current.TextDim),
        };
        var content = new StackPanel();
        content.Children.Add(_previewFloat);
        content.Children.Add(hint);

        var card = new Border
        {
            Child = content,
            Width = 236,
            Padding = new Thickness(7, 7, 7, 6),
            CornerRadius = new CornerRadius(12),
            Background = Theme.Brush(Theme.Current.Surface),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            RenderTransform = _slide,
            Cursor = Cursors.SizeAll,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.32,
                Color = Colors.Black,
            },
        };
        card.MouseLeftButtonDown += PreviewDragStart;
        card.MouseMove += PreviewDragMove;
        card.MouseLeftButtonUp += PreviewDragEnd;
        return card;
    }

    /// <summary>Floats the preview only while the inline one is out of the viewport.</summary>
    void SyncFloatingPreview()
    {
        if (_previewSlot is null || !IsLoaded) return;
        bool away;
        try
        {
            var where = _previewSlot.TransformToAncestor(_scroll).Transform(new Point(0, 0));
            away = where.Y + _previewSlot.ActualHeight < 8;
        }
        catch (InvalidOperationException)
        {
            return; // not in the visual tree yet
        }
        ShowFloatingPreview(away);
    }

    void ShowFloatingPreview(bool show)
    {
        if (_floatShown == show) return;
        _floatShown = show;
        if (show)
        {
            PlaceFloating();
            _floatCard.Visibility = Visibility.Visible;
            _floatCard.IsHitTestVisible = true;
            _slide.Y = 14;
            FadeFloating(1, 0, 190);
        }
        else
        {
            _floatCard.IsHitTestVisible = false;
            FadeFloating(0, 10, 140);
        }
    }

    /// <summary>
    /// Moves the card to a new opacity and offset. The destination is written as the base value
    /// and the animation runs with FillBehavior.Stop, so no animation stays attached afterwards
    /// and pins the property the way a held animation would.
    /// </summary>
    void FadeFloating(double opacity, double offset, int ms)
    {
        _floatCard.BeginAnimation(OpacityProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        var fromOpacity = _floatCard.Opacity;
        var fromOffset = _slide.Y;
        _floatCard.Opacity = opacity;
        _slide.Y = offset;
        if (!IsLoaded || ms <= 0)
        {
            if (opacity == 0) _floatCard.Visibility = Visibility.Collapsed;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(fromOpacity, opacity, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        if (opacity == 0)
            fade.Completed += (_, _) => { if (!_floatShown) _floatCard.Visibility = Visibility.Collapsed; };
        _floatCard.BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromOffset, offset, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
    }

    /// <summary>The area the preview may occupy: the scrolling page, not the header above it.</summary>
    Rect ScrollArea()
    {
        try
        {
            var origin = _scroll.TransformToAncestor(_floatLayer).Transform(new Point(0, 0));
            return new Rect(origin, new Size(_scroll.ActualWidth, _scroll.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return new Rect(0, 0, Math.Max(1, _floatLayer.ActualWidth), Math.Max(1, _floatLayer.ActualHeight));
        }
    }

    Point DockPoint()
    {
        var area = ScrollArea();
        var width = _floatCard.ActualWidth > 0 ? _floatCard.ActualWidth : _floatCard.Width;
        var height = _floatCard.ActualHeight > 0 ? _floatCard.ActualHeight : 200;
        const double gap = 16;
        var onRight = _s.PreviewDock is PreviewCorner.TopRight or PreviewCorner.BottomRight;
        var atBottom = _s.PreviewDock is PreviewCorner.BottomLeft or PreviewCorner.BottomRight;
        var x = onRight ? area.Right - width - gap : area.Left + gap;
        var y = atBottom ? area.Bottom - height - gap : area.Top + gap;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        return new Point(x, y);
    }

    void PlaceFloating()
    {
        var target = DockPoint();
        _floatCard.BeginAnimation(Canvas.LeftProperty, null);
        _floatCard.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(_floatCard, target.X);
        Canvas.SetTop(_floatCard, target.Y);
    }

    void PreviewDragStart(object sender, MouseButtonEventArgs e)
    {
        _draggingPreview = true;
        _dragGrab = e.GetPosition(_floatCard);
        _floatCard.CaptureMouse();
        e.Handled = true;
    }

    void PreviewDragMove(object sender, MouseEventArgs e)
    {
        if (!_draggingPreview) return;
        var at = e.GetPosition(_floatLayer);
        var area = ScrollArea();
        var width = _floatCard.ActualWidth;
        var height = _floatCard.ActualHeight;
        var x = Math.Clamp(at.X - _dragGrab.X, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(at.Y - _dragGrab.Y, area.Top, Math.Max(area.Top, area.Bottom - height));
        Canvas.SetLeft(_floatCard, x);
        Canvas.SetTop(_floatCard, y);
    }

    void PreviewDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingPreview) return;
        _draggingPreview = false;
        _floatCard.ReleaseMouseCapture();

        // Dropping it in a part of the window picks that corner, and the choice is remembered.
        var area = ScrollArea();
        var centre = new Point(Canvas.GetLeft(_floatCard) + _floatCard.ActualWidth / 2,
                               Canvas.GetTop(_floatCard) + _floatCard.ActualHeight / 2);
        _s.PreviewDock = DockAt(centre, area);
        Commit();
        SnapFloating();
    }

    /// <summary>Which corner a drop point means. Kept apart from the drag so it can be checked.</summary>
    internal static PreviewCorner DockAt(Point centre, Rect area) =>
        centre.Y > area.Top + area.Height / 2
            ? (centre.X > area.Left + area.Width / 2 ? PreviewCorner.BottomRight : PreviewCorner.BottomLeft)
            : (centre.X > area.Left + area.Width / 2 ? PreviewCorner.TopRight : PreviewCorner.TopLeft);

    /// <summary>Re-reads the chosen corner and slides the card there if it is on screen.</summary>
    internal void RefreshDock()
    {
        if (_floatShown) SnapFloating();
    }

    /// <summary>Where the floating card currently sits, in window coordinates.</summary>
    internal Point PreviewPosition => new(Canvas.GetLeft(_floatCard), Canvas.GetTop(_floatCard));

    /// <summary>Slides the card from wherever it was dropped to its corner.</summary>
    void SnapFloating()
    {
        var target = DockPoint();
        var fromX = Canvas.GetLeft(_floatCard);
        var fromY = Canvas.GetTop(_floatCard);
        Canvas.SetLeft(_floatCard, target.X);
        Canvas.SetTop(_floatCard, target.Y);
        if (!IsLoaded)
        {
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ms = TimeSpan.FromMilliseconds(200);
        _floatCard.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(fromX, target.X, ms) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        _floatCard.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(fromY, target.Y, ms) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    void DrawPreview()
    {
        _preview.Stop();
        try
        {
            var menu = new RadialMenu { Look = _s };
            var payload = new DragPayload(new[] { Path.Combine(Path.GetTempPath(), "preview.png") });
            var ring = ActionCatalog.Build(payload, MenuTab.Convert, DragDone.Core.Capabilities.All);
            menu.Items = ring.Take(6).ToList();
            menu.SetHub(Loc.T("kind.image"), Loc.Size(2_400_000), 1);
            var size = menu.Geometry.Size;
            menu.Measure(new Size(size, size));
            menu.Arrange(new Rect(0, 0, size, size));
            menu.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)(size * dpi.DpiScaleX), (int)(size * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var root = new DrawingVisual();
            using (var dc = root.RenderOpen())
            {
                dc.DrawRectangle(Theme.Brush(Theme.Current.SurfaceAlt), null, new Rect(0, 0, size, size));
                dc.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, size, size));
            }
            bitmap.Render(root);
            bitmap.Freeze();
            _previewInline.Source = bitmap;
            _previewFloat.Source = bitmap;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine(ex);
        }
    }

    // ------------------------------------------------------------------ ring

    UIElement Ring()
    {
        return Card(
            Row("settings.layout", "settings.layout.desc", Choose(
                new[] { MenuLayout.Auto, MenuLayout.Donut, MenuLayout.Pie, MenuLayout.Orbit }, _s.Layout,
                v => _s.Layout = v, v => Loc.T("layout." + v.ToString().ToLowerInvariant()))),
            Row("settings.density", "settings.density.desc", Choose(
                new[] { Density.Compact, Density.Roomy, Density.Large }, _s.Density,
                v => _s.Density = v, v => Loc.T("density." + v.ToString().ToLowerInvariant()))),
            Divider(),
            Slider("settings.diameter", "settings.diameter.desc", 240, 520, 4, _s.MenuDiameter, v => _s.MenuDiameter = v,
                v => Loc.F("settings.px", ("n", (int)Math.Round(v)))),
            Slider("settings.gap", "settings.gap.desc", 0, 16, 1, _s.Gap, v => _s.Gap = v, v => Loc.Digits(((int)v).ToString())),
            Slider("settings.frame", "settings.frame.desc", 0, 4, 0.2, _s.FrameThickness, v => _s.FrameThickness = v,
                v => v == 0 ? Loc.T("settings.frame.none") : Loc.Digits(v.ToString("0.#"))),
            Slider("settings.hover", "settings.hover.desc", 0, 2, 0.1, _s.HoverGrow, v => _s.HoverGrow = v,
                v => Loc.Digits(Math.Round(v * 100).ToString("0")) + "%"),
            Divider(),
            Row("settings.labels", "settings.labels.desc", Choose(
                new[] { LabelStyle.Auto, LabelStyle.Icons, LabelStyle.Labels, LabelStyle.Text }, _s.Labels,
                v => _s.Labels = v, v => Loc.T("labels." + v.ToString().ToLowerInvariant()))),
            Row("settings.rotated", "settings.rotated.desc", Toggle(_s.RotatedLabels, v => _s.RotatedLabels = v)),
            Row("settings.tabBar", "settings.tabBar.desc", Toggle(_s.ShowTabBar, v => _s.ShowTabBar = v)));
    }

    // ------------------------------------------------------------------ shortcuts

    UIElement Shortcuts()
    {
        var legend = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        // "None" comes first so a tab can be switched off, and so a binding that no longer works
        // can always be replaced from here.
        var keyOptions = new List<string> { Keys.None };
        keyOptions.AddRange(Keys.All);

        foreach (var tab in ActionCatalog.TabOrder)
        {
            var current = BoundKeys.TabKey(_s, tab);
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = Loc.T("tab." + tab.ToString().ToLowerInvariant()), FontSize = 13.5, FlowDirection = FlowDirection.LeftToRight, Foreground = Theme.Brush(Theme.Current.Text) });
            text.Children.Add(Muted(Loc.T("settings.legend." + tab.ToString().ToLowerInvariant())));
            grid.Children.Add(text);

            var box = Choose(keyOptions, current, key => BoundKeys.Set(_s, tab, key), Keys.Label);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var row = new StackPanel();
            row.Children.Add(grid);
            if (Keys.IsMouse(current))
                row.Children.Add(Muted(Loc.T("settings.mouseKey.note"), 11.5));
            legend.Children.Add(row);
            _searchable.Add((grid, Loc.T("tab." + tab.ToString().ToLowerInvariant()) + " " + Loc.T("settings.legend." + tab.ToString().ToLowerInvariant()) + " shortcut"));
        }

        var warn = Muted(Loc.T("settings.keys.warn"), 11.5);
        return Card(
            legend,
            warn,
            Divider(),
            Row("settings.triggerMode", "settings.triggerMode.desc", Choose(
                new[] { TriggerMode.Modifier, TriggerMode.ModifierOrPause }, _s.Trigger,
                v => _s.Trigger = v, v => Loc.T("trigger." + v.ToString().ToLowerInvariant()))),
            Slider("settings.dwell", "settings.dwell.desc", 80, 900, 20, _s.DwellMs, v => _s.DwellMs = (int)v,
                v => Loc.F("settings.ms", ("n", (int)Math.Round(v)))),
            Divider(),
            Row("settings.scope", "settings.scope.desc", Choose(
                new[] { SourceScope.Shell, SourceScope.Anywhere }, _s.Scope,
                v => _s.Scope = v, v => Loc.T("scope." + v.ToString().ToLowerInvariant()))));
    }

    // ------------------------------------------------------------------ general

    UIElement General()
    {
        var lang = Choose(
            new[] { LanguageMode.System, LanguageMode.Arabic, LanguageMode.English },
            _s.LanguageMode,
            v => _s.LanguageMode = v,
            v => v == LanguageMode.System ? Loc.T("lang.system") : v == LanguageMode.Arabic ? "العربية" : "English");
        return Card(
            Row("settings.language", "settings.language.desc", lang),
            Divider(),
            Row("settings.startup", "settings.startup.desc", Toggle(_s.StartWithWindows, v => _s.StartWithWindows = v)),
            Row("settings.paused", "settings.paused.desc", Toggle(_s.Paused, v => _s.Paused = v)));
    }

    UIElement Output()
    {
        var folderText = Muted(string.IsNullOrEmpty(_s.OutputFolder) ? Loc.T("settings.output.besideHint") : _s.OutputFolder!, 12);
        folderText.FlowDirection = FlowDirection.LeftToRight;
        folderText.TextAlignment = Loc.IsRtl ? TextAlignment.Right : TextAlignment.Left;

        var beside = new RadioButton { GroupName = "out", Content = Loc.T("settings.output.beside"), IsChecked = string.IsNullOrEmpty(_s.OutputFolder), Margin = new Thickness(0, 3, 0, 3), Foreground = Theme.Brush(Theme.Current.Text) };
        var fixedFolder = new RadioButton { GroupName = "out", Content = Loc.T("settings.output.folder"), IsChecked = !string.IsNullOrEmpty(_s.OutputFolder), Margin = new Thickness(0, 3, 0, 3), Foreground = Theme.Brush(Theme.Current.Text) };
        beside.Checked += (_, _) =>
        {
            _s.OutputFolder = null;
            Commit();
            Rebuild();
        };
        fixedFolder.Checked += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_s.OutputFolder)) return;
            PickFolder();
        };
        var change = PlainButton(Loc.T("settings.change"), (_, _) => PickFolder());
        change.Margin = new Thickness(0, 6, 0, 0);
        change.HorizontalAlignment = HorizontalAlignment.Left;
        change.Visibility = string.IsNullOrEmpty(_s.OutputFolder) ? Visibility.Collapsed : Visibility.Visible;

        return Card(
            new TextBlock { Text = Loc.T("settings.output.where"), Foreground = Theme.Brush(Theme.Current.Text) },
            beside,
            fixedFolder,
            folderText,
            change,
            Divider(),
            Slider("settings.jpegQuality", "settings.jpegQuality.desc", 50, 100, 1, _s.JpegQuality, v => _s.JpegQuality = (int)v, v => Loc.Digits(((int)v).ToString())),
            Slider("settings.resultSeconds", "settings.resultSeconds.desc", 2, 15, 1, _s.ResultSeconds, v => _s.ResultSeconds = (int)v,
                v => Loc.F("settings.seconds", ("n", (int)v))));
    }

    void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("settings.output.pick") };
        if (dlg.ShowDialog(this) == true) _s.OutputFolder = dlg.FolderName;
        Commit();
        Rebuild();
    }

    UIElement Tools()
    {
        var ff = Ffmpeg.Locate(_s.FfmpegPath);
        var status = new StackPanel();
        status.Children.Add(StatusLine(ff is not null, ff is not null ? Loc.T("settings.ffmpeg.found") : Loc.T("settings.ffmpeg.missing")));
        if (ff is not null)
        {
            var path = Muted(ff, 11.5);
            path.FlowDirection = FlowDirection.LeftToRight;
            path.TextAlignment = Loc.IsRtl ? TextAlignment.Right : TextAlignment.Left;
            status.Children.Add(path);
        }
        else
        {
            status.Children.Add(Muted(Loc.T("settings.ffmpeg.how"), 12));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(PlainButton(Loc.T("settings.ffmpeg.browse"), (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|*.exe|*.exe", Title = Loc.T("settings.ffmpeg.browse") };
            if (dlg.ShowDialog(this) != true) return;
            _s.FfmpegPath = dlg.FileName;
            Ffmpeg.ResetCache();
            Commit();
            Rebuild();
        }));
        if (!string.IsNullOrEmpty(_s.FfmpegPath))
        {
            var reset = PlainButton(Loc.T("settings.ffmpeg.auto"), (_, _) =>
            {
                _s.FfmpegPath = null;
                Ffmpeg.ResetCache();
                Commit();
                Rebuild();
            });
            reset.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(reset);
        }
        status.Children.Add(buttons);

        var office = DocumentOps.OfficeAvailable;
        var card = Card(
            new TextBlock { Text = "FFmpeg", FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Foreground = Theme.Brush(Theme.Current.Text) },
            Muted(Loc.T("settings.ffmpeg.desc"), 12),
            status,
            Divider(),
            new TextBlock { Text = "Microsoft Office", FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Foreground = Theme.Brush(Theme.Current.Text) },
            Muted(Loc.T("settings.office.desc"), 12),
            StatusLine(office, office ? Loc.T("settings.office.found") : Loc.T("settings.office.missing")));
        _searchable.Add((card, "FFmpeg Microsoft Office"));
        return card;
    }

    static UIElement StatusLine(bool ok, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = ok ? "\uE73E" : "\uE7BA",
            FontFamily = Theme.IconFont,
            Foreground = Theme.Brush(ok ? Theme.Current.Success : Theme.Current.Warning),
            Margin = new Thickness(0, 2, 8, 0),
        });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush(Theme.Current.Text) });
        return row;
    }

    // ------------------------------------------------------------------ themes on disk

    UIElement Themes()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var export = PlainButton(Loc.T("settings.theme.export"), (_, _) => ExportTheme());
        var import = PlainButton(Loc.T("settings.theme.import"), (_, _) => ImportTheme());
        import.Margin = new Thickness(8, 0, 0, 0);
        var reset = PlainButton(Loc.T("settings.theme.reset"), (_, _) => ResetTheme());
        reset.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        buttons.Children.Add(reset);

        var row = Card(Muted(Loc.T("settings.theme.files.desc"), 12), buttons);
        _searchable.Add((row, Loc.T("settings.theme.export") + " " + Loc.T("settings.theme.import") + " theme file"));
        return row;
    }

    void ExportTheme()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("settings.theme.export"),
            FileName = $"DragDone-{_s.ThemePreset}.theme.json",
            Filter = "DragDone theme|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _s.ExportTheme(dlg.FileName);
            _info = Loc.T("settings.theme.exported");
        }
        catch (IOException)
        {
            _info = Loc.T("settings.theme.failed");
        }
        Rebuild();
    }

    void ImportTheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("settings.theme.import"), Filter = "DragDone theme|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _info = _s.ImportThemeJson(File.ReadAllText(dlg.FileName)) ? Loc.T("settings.theme.imported") : Loc.T("settings.theme.failed");
            Commit();
        }
        catch (IOException)
        {
            _info = Loc.T("settings.theme.failed");
        }
        Theme.Apply();
        Rebuild();
    }

    void ResetTheme()
    {
        var fresh = new Settings();
        Look.From(fresh).CopyTo(_s);
        _info = Loc.T("settings.theme.reset.done");
        Commit();
        Theme.Apply();
        Rebuild();
    }

    string? _info;

    UIElement About()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var open = PlainButton(Loc.T("settings.openFolder"), (_, _) =>
        {
            Directory.CreateDirectory(Settings.Folder);
            Process.Start(new ProcessStartInfo(Settings.Folder) { UseShellExecute = true });
        });
        open.HorizontalAlignment = HorizontalAlignment.Left;
        var welcome = PlainButton(Loc.T("settings.showWelcome"), (_, _) => new WelcomeWindow(null).Show());
        welcome.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(welcome);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Loc.F("settings.version", ("v", Loc.Digits(version))), Foreground = Theme.Brush(Theme.Current.Text) });
        stack.Children.Add(Muted(Loc.T("settings.offline")));
        if (_info is not null) stack.Children.Add(StatusLine(true, _info));
        stack.Children.Add(buttons);
        return Card(stack);
    }
}
