using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>
/// The shelf: a small always-on-top tray that holds files so they can be dragged out later
/// (to another folder, a browser upload, a chat). Files can be dropped onto it at any time.
/// </summary>
public sealed class ShelfWindow : FluentWindow
{
    static ShelfWindow? _instance;

    readonly ObservableCollection<string> _items = new();
    readonly WrapPanel _list = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 0, -4, 0) };
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    readonly Border _dragAll;
    readonly TextBlock _dragAllLabel = new() { Foreground = Theme.Brush(Theme.On(Theme.Current.Accent)), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _hint = new();
    Button? _clearButton;
    Point _pressAt;
    string[]? _pressItems;

    public static ShelfWindow Instance => _instance ??= new ShelfWindow();

    ShelfWindow()
    {
        Title = Loc.T("shelf.title");
        Width = 344;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 520;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        AllowDrop = true;
        CloseOnEscape = false;

        _dragAll = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 7),
            Background = Theme.Brush(Theme.Current.Accent),
            Cursor = Cursors.Hand,
            Child = _dragAllLabel,
        };
        HookDragSource(_dragAll, () => _items.ToArray());

        _clearButton = PlainButton(Loc.T("shelf.clear"), (_, _) => _items.Clear());
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_clearButton, Dock.Right);
        top.Children.Add(_clearButton);
        top.Children.Add(_count);

        var scroller = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 360 };
        var root = new StackPanel { Margin = new Thickness(12, 10, 12, 14) };
        root.Children.Add(top);
        root.Children.Add(scroller);
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(_dragAll);
        root.Children.Add(_hint);
        Content = root;

        _items.CollectionChanged += (_, _) => Refresh();
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
            e.Handled = true;
        };
        Closing += (_, e) =>
        {
            // Keep the instance (and its files) alive; closing just hides the shelf.
            e.Cancel = true;
            Hide();
        };
        Relabel();
        Refresh();
    }

    /// <summary>Re-writes the labels that were placed in the current language.</summary>
    protected override void OnLanguageReloaded()
    {
        Title = Loc.T("shelf.title");
        Relabel();
        Refresh();
    }

    void Relabel()
    {
        _dragAllLabel.Text = Loc.T("shelf.dragAll");
        _dragAll.ToolTip = Loc.T("shelf.dragAll.tip");
        if (_clearButton is not null) _clearButton.Content = Loc.T("shelf.clear");
        _hint.Text = Loc.T("shelf.hint");
        _hint.FontSize = 11.5;
        _hint.Opacity = 0.7;
        // The hint is a full sentence; without wrapping it is clipped by the narrow shelf.
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 6, 0, 0);
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!_items.Contains(p, StringComparer.OrdinalIgnoreCase)) _items.Add(p);
        }
    }

    /// <summary>The panel the cards are laid out in. Used by the self-test.</summary>
    internal WrapPanel CardHost => _list;

    /// <summary>How many files the shelf is holding.</summary>
    internal int ItemCount => _items.Count;

    /// <summary>Drops the first item. Used by the self-test.</summary>
    internal void RemoveFirst()
    {
        if (_items.Count > 0) _items.RemoveAt(0);
    }

    /// <summary>Empties the shelf. Used by the self-test.</summary>
    internal void Clear() => _items.Clear();

    public void ShowNear(Native.Win32.POINT cursor)
    {
        if (!IsVisible)
        {
            var source = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var p = source.Transform(new Point(cursor.X, cursor.Y));
            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(p.X + 24, work.Left, work.Right - Width);
            Top = Math.Clamp(p.Y - 60, work.Top, work.Bottom - 300);
            Show();
        }
        FlowDirection = Loc.Flow;
    }

    void Refresh()
    {
        // Files moved or deleted elsewhere disappear from the shelf.
        foreach (var gone in _items.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()) _items.Remove(gone);
        _list.Children.Clear();
        foreach (var path in _items) _list.Children.Add(ItemCard(path));
        _count.Text = _items.Count == 0 ? Loc.T("shelf.empty") : Loc.F("shelf.count", ("n", _items.Count));
        _dragAll.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One file, shown as a card: a preview, the name, and the menu the clipboard puts in its
    /// corner. Cards flow into columns, so the shelf reads as a grid rather than a list.
    /// </summary>
    UIElement ItemCard(string path)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(3),
            Child = new Image { Source = ThumbFor(path), Stretch = Stretch.Uniform, Height = 50 },
        };

        var name = new TextBlock
        {
            Text = Path.GetFileName(path.TrimEnd('\\')),
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Theme.Brush(Theme.Current.Text),
        };

        var menu = ItemMenu(path);
        var more = new Button
        {
            Content = new TextBlock { Text = "\uE712", FontFamily = Theme.IconFont, FontSize = 10 },
            Width = 20,
            Height = 18,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Opacity = 0.45,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("shelf.more"),
        };
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        // Right-clicking the glyph opens the same menu, and gives the card an inspectable one.
        more.ContextMenu = menu;

        var body = new StackPanel();
        body.Children.Add(frame);
        body.Children.Add(name);

        var stack = new Grid();
        stack.Children.Add(body);
        stack.Children.Add(more);

        var card = new Border
        {
            Width = 142,
            Margin = new Thickness(4),
            Padding = new Thickness(6, 6, 6, 7),
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.Current.SurfaceAlt),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = path,
            Child = stack,
        };

        // The corner menu belongs to the card being pointed at, like the clipboard's does.
        card.MouseEnter += (_, _) =>
        {
            card.Background = Theme.Brush(Theme.Current.Surface);
            card.BorderBrush = Theme.Accent;
            more.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Theme.Brush(Theme.Current.SurfaceAlt);
            card.BorderBrush = Theme.Brush(Theme.Current.Stroke);
            more.Opacity = 0.45;
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) OpenItem(path);
        };
        HookDragSource(card, () => new[] { path });
        return card;
    }

    /// <summary>Open, reveal, or drop one item — the same actions the overlay's result card offers.</summary>
    ContextMenu ItemMenu(string path)
    {
        var menu = new ContextMenu { FlowDirection = Loc.Flow };
        menu.Items.Add(MenuAction(Loc.T("shelf.open"), () => OpenItem(path)));
        menu.Items.Add(MenuAction(Loc.T("shelf.reveal"), () => OverlayWindow.Reveal(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction(Loc.T("shelf.remove"), () => _items.Remove(path)));
        return menu;
    }

    static MenuItem MenuAction(string text, Action run)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => run();
        return item;
    }

    static void OpenItem(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // The file went away between the shelf showing it and the click.
        }
    }

    void HookDragSource(UIElement element, Func<string[]> items)
    {
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressAt = e.GetPosition(this);
            _pressItems = items();
        };
        element.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressItems is not { Length: > 0 }) return;
            var d = e.GetPosition(this) - _pressAt;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var data = new DataObject(DataFormats.FileDrop, _pressItems);
            var dragged = _pressItems;
            _pressItems = null;
            var effect = DragDrop.DoDragDrop(element, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (effect == DragDropEffects.Move) foreach (var p in dragged) _items.Remove(p);
            Refresh();
        };
    }

    /// <summary>A picture thumbnail for images, the shell icon for everything else.</summary>
    static ImageSource? ThumbFor(string path)
    {
        if (Directory.Exists(path)) return GlyphImage(Glyphs.Folder);
        if (FileKinds.Of(path) == FileKind.Image)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 168;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException
                                        or UnauthorizedAccessException or FileFormatException)
            {
                // A broken or unreadable image still has an icon; fall through to it.
            }
        }
        return IconFor(path);
    }

    static ImageSource? IconFor(string path)
    {
        try
        {
            if (Directory.Exists(path)) return GlyphImage(Glyphs.Folder);
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static ImageSource GlyphImage(string glyph)
    {
        var brush = Theme.Brush(Color.FromRgb(0xF5, 0xC5, 0x4A)); // Explorer folder yellow
        var ft = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Theme.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, brush, 1);
        var geometry = ft.BuildGeometry(new Point(0, 0));
        geometry.Freeze();
        var img = new DrawingImage(new GeometryDrawing(brush, null, geometry));
        img.Freeze();
        return img;
    }
}
