using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>
/// Free crop: draw, move or resize a rectangle on the first image, optionally locked to an
/// aspect ratio. Returns the selection as fractions so it applies to every dragged image.
/// </summary>
public sealed class CropWindow : FluentWindow
{
    const double Handle = 12;

    readonly Canvas _canvas = new() { ClipToBounds = true };
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Fill = Brushes.Transparent, Cursor = Cursors.SizeAll };
    readonly Rectangle[] _handles = new Rectangle[4];
    readonly TextBlock _sizeText = new() { FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
    readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.7 };
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBlock _applyLabel = new();
    readonly string _fileName;
    readonly int _count;
    readonly int _pxW, _pxH;
    readonly double _viewW, _viewH;
    Rect _sel;
    double? _ratio;
    Point _dragStart;
    Rect _selAtStart;
    int _mode = -2; // -2 none, -1 move, 0..3 corner, 4 new

    /// <summary>Selection as fractions of width/height; null when cancelled.</summary>
    public Rect? Result { get; private set; }

    public CropWindow(BitmapSource image, string fileName, int count)
    {
        _fileName = fileName;
        _count = count;
        _pxW = image.PixelWidth;
        _pxH = image.PixelHeight;
        Title = Loc.T("crop.title") + " — " + fileName;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var work = SystemParameters.WorkArea;
        // One uniform scale, so view-space ratios equal pixel ratios.
        var scale = new[] { 1.0, (work.Width - 120) / _pxW, (work.Height - 240) / _pxH, 960.0 / _pxW, 680.0 / _pxH }.Min();
        _viewW = Math.Max(120, Math.Round(_pxW * scale));
        _viewH = Math.Max(120, Math.Round(_pxH * scale));

        _image.Source = image;
        _image.Width = _viewW;
        _image.Height = _viewH;
        _canvas.Width = _viewW;
        _canvas.Height = _viewH;
        _canvas.FlowDirection = FlowDirection.LeftToRight; // image coordinates never mirror
        _canvas.Background = Brushes.Black;
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_frame);
        for (var i = 0; i < 4; i++)
        {
            _handles[i] = new Rectangle
            {
                Width = Handle,
                Height = Handle,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
                RadiusX = 2,
                RadiusY = 2,
                Cursor = i is 0 or 3 ? Cursors.SizeNWSE : Cursors.SizeNESW,
                Tag = i,
            };
            _canvas.Children.Add(_handles[i]);
        }
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _mode = -2;
            _canvas.ReleaseMouseCapture();
        };

        _sel = new Rect(_viewW * 0.1, _viewH * 0.1, _viewW * 0.8, _viewH * 0.8);

        var toolbar = _toolbar;
        toolbar.Children.Clear();
        foreach (var (label, ratio) in new (string, double?)[] { (Loc.T("crop.free"), null), ("1:1", 1), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0) })
        {
            var b = new ToggleButtonLike(ratio is null ? "crop.free" : null, label, ratio is null);
            b.Click += (_, _) =>
            {
                foreach (var other in toolbar.Children.OfType<ToggleButtonLike>()) other.IsOn = false;
                b.IsOn = true;
                SetRatio(ratio);
            };
            toolbar.Children.Add(b);
        }

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        footer.Children.Add(_sizeText);
        var apply = AccentButton(count > 1 ? Loc.F("crop.applyMany", ("n", count)) : Loc.T("crop.apply"), (_, _) => Apply());
        apply.IsDefault = true;
        var cancel = PlainButton(Loc.T("common.cancel"), (_, _) => Close());
        cancel.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(apply, Dock.Right);
        footer.Children.Add(cancel);
        footer.Children.Add(apply);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(toolbar);
        root.Children.Add(new Border { Child = _canvas, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(_hint);
        root.Children.Add(footer);
        Content = root;
        _hint.Text = Loc.T("crop.hint");
        Layout();
    }

    /// <summary>Re-writes the labels that were placed in the current language.</summary>
    protected override void OnLanguageReloaded()
    {
        Title = Loc.T("crop.title") + " — " + _fileName;
        _hint.Text = Loc.T("crop.hint");
        foreach (var b in _toolbar.Children.OfType<ToggleButtonLike>()) b.Reload();
    }

    void SetRatio(double? ratio)
    {
        _ratio = ratio;
        if (ratio is null) return;
        // Largest rectangle of that ratio within 90 % of the image, centred on the current selection.
        double w = _viewW * 0.9, h = w / ratio.Value;
        if (h > _viewH * 0.9)
        {
            h = _viewH * 0.9;
            w = h * ratio.Value;
        }
        var c = new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2);
        _sel = Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
        Layout();
    }

    double HeightFor(double w) => w / _ratio!.Value;

    Rect Clamp(Rect r)
    {
        var w = Math.Min(r.Width, _viewW);
        var h = Math.Min(r.Height, _viewH);
        var x = Math.Clamp(r.X, 0, _viewW - w);
        var y = Math.Clamp(r.Y, 0, _viewH - h);
        return new Rect(x, y, w, h);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        _dragStart = p;
        _selAtStart = _sel;
        _mode = e.OriginalSource is Rectangle { Tag: int corner } ? corner : _sel.Contains(p) ? -1 : 4;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_mode == -2 || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        p = new Point(Math.Clamp(p.X, 0, _viewW), Math.Clamp(p.Y, 0, _viewH));
        var d = p - _dragStart;
        switch (_mode)
        {
            case -1:
                _sel = Clamp(new Rect(_selAtStart.X + d.X, _selAtStart.Y + d.Y, _selAtStart.Width, _selAtStart.Height));
                break;
            case 4:
                _sel = FromCorners(_dragStart, p);
                break;
            default:
            {
                // The corner opposite to the one dragged stays fixed.
                var anchor = _mode switch
                {
                    0 => _selAtStart.BottomRight,
                    1 => _selAtStart.BottomLeft,
                    2 => _selAtStart.TopRight,
                    _ => _selAtStart.TopLeft,
                };
                _sel = FromCorners(anchor, p);
                break;
            }
        }
        Layout();
    }

    Rect FromCorners(Point anchor, Point p)
    {
        var w = Math.Max(8, Math.Abs(p.X - anchor.X));
        var h = Math.Max(8, Math.Abs(p.Y - anchor.Y));
        if (_ratio is not null)
        {
            var hr = HeightFor(w);
            if (hr > h) w = h / hr * w;
            h = HeightFor(w);
        }
        var x = p.X < anchor.X ? anchor.X - w : anchor.X;
        var y = p.Y < anchor.Y ? anchor.Y - h : anchor.Y;
        var r = new Rect(x, y, w, h);
        r.Intersect(new Rect(0, 0, _viewW, _viewH));
        return r.IsEmpty ? _sel : r;
    }

    void Layout()
    {
        Canvas.SetLeft(_frame, _sel.X);
        Canvas.SetTop(_frame, _sel.Y);
        _frame.Width = _sel.Width;
        _frame.Height = _sel.Height;
        var corners = new[] { _sel.TopLeft, _sel.TopRight, _sel.BottomLeft, _sel.BottomRight };
        for (var i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_handles[i], corners[i].X - Handle / 2);
            Canvas.SetTop(_handles[i], corners[i].Y - Handle / 2);
        }
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _viewW, _viewH)), new RectangleGeometry(_sel));
        var pw = (int)Math.Round(_sel.Width / _viewW * _pxW);
        var ph = (int)Math.Round(_sel.Height / _viewH * _pxH);
        _sizeText.Text = $"{pw} × {ph} px";
    }

    void Apply()
    {
        Result = new Rect(_sel.X / _viewW, _sel.Y / _viewH, _sel.Width / _viewW, _sel.Height / _viewH);
        DialogResult = true;
    }

    /// <summary>Pill used for the ratio presets.</summary>
    sealed class ToggleButtonLike : Button
    {
        bool _on;
        readonly string? _key;
        readonly string _literal;

        public ToggleButtonLike(string text, bool on)
            : this(null, text, on) { }

        public ToggleButtonLike(string? key, string text, bool on)
        {
            _key = key;
            _literal = text;
            Content = text;
            Margin = new Thickness(0, 0, 6, 0);
            Padding = new Thickness(12, 4, 12, 5);
            IsOn = on;
        }

        /// <summary>Re-writes the label in the current language.</summary>
        public void Reload() => Content = _key is null ? _literal : Loc.T(_key);

        public bool IsOn
        {
            get => _on;
            set
            {
                _on = value;
                if (value) SetResourceReference(StyleProperty, "AccentButtonStyle");
                else ClearValue(StyleProperty);
            }
        }
    }
}
