using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>
/// Chooses an exact accent colour: a hue strip, a saturation and brightness field, a hex box, the
/// named presets and a live swatch.
///
/// Windows' own colour dialog is a Windows Forms control, and this app no longer carries Windows
/// Forms. Building it here also means it speaks the app's language, follows its palette, and takes
/// a colour in the one format the settings file stores.
/// </summary>
public sealed class ColorPickerWindow : FluentWindow
{
    const double FieldWidth = 248;
    const double FieldHeight = 156;
    const double HueHeight = 14;
    const double ThumbRadius = 7;

    readonly LinearGradientBrush _fieldFill = FieldFill();
    readonly LinearGradientBrush _hueFill = HueFill();
    readonly Canvas _fieldLayer = new();
    readonly Ellipse _fieldThumb = new()
    {
        Width = ThumbRadius * 2,
        Height = ThumbRadius * 2,
        Stroke = Brushes.White,
        StrokeThickness = 2,
        IsHitTestVisible = false,
    };
    readonly Canvas _hueLayer = new();
    readonly Border _hueThumb = new()
    {
        Width = 5,
        Height = HueHeight + 6,
        CornerRadius = new CornerRadius(2.5),
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1.5),
        IsHitTestVisible = false,
    };
    readonly TextBox _hex = new()
    {
        Width = 104,
        // A hex value is Latin and reads left to right whatever the interface language is.
        FlowDirection = FlowDirection.LeftToRight,
        FontFamily = new FontFamily("Consolas, Segoe UI"),
        FontSize = 13,
        Padding = new Thickness(6, 4, 6, 5),
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    readonly Border _preview = new()
    {
        Width = 52,
        Height = 52,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
    };
    readonly TextBlock _previewText = new()
    {
        Text = "Aa",
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBlock _desc = FluentWindow.Muted(Loc.T("accent.custom.desc"));
    readonly TextBlock _hueLabel = Caption(Loc.T("accent.custom.hue"));
    readonly TextBlock _hexLabel = Caption(Loc.T("accent.custom.hex"));
    readonly TextBlock _presetsLabel = Caption(Loc.T("accent.custom.presets"));
    readonly TextBlock _previewLabel = Caption(Loc.T("accent.custom.preview"));
    readonly TextBlock _problem = new()
    {
        Text = Loc.T("accent.custom.invalid"),
        FontSize = 12,
        Margin = new Thickness(0, 4, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    Button? _ok;
    Button? _cancel;
    bool _syncing;
    double _hue;
    double _saturation;
    double _brightness;
    Color _colour;

    /// <summary>The colour the user settled on. Only meaningful once the dialog was accepted.</summary>
    public Color Selected { get; private set; }

    public ColorPickerWindow(Color initial)
    {
        Title = Loc.T("accent.custom.title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        _colour = initial;
        (_hue, _saturation, _brightness) = ToHsv(initial);
        _problem.Foreground = Theme.Brush(Theme.Current.Danger);
        _preview.BorderBrush = Theme.Brush(Theme.Current.Stroke);
        _preview.Child = _previewText;
        _hex.TextChanged += (_, _) => OnHexTyped();
        _hex.LostFocus += (_, _) => NormaliseHex();

        // Saturation runs left to right and brightness bottom to top. Both fields read the pointer
        // in their own coordinates, so they stay laid out left to right in either language.
        var shade = new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(0, 1));
        var fieldHost = Area(FieldWidth, FieldHeight, 6, _fieldFill, shade, _fieldLayer, _fieldThumb);
        fieldHost.Margin = new Thickness(0, 10, 0, 0);
        fieldHost.Cursor = Cursors.Cross;
        fieldHost.MouseLeftButtonDown += (_, e) =>
        {
            fieldHost.CaptureMouse();
            PickField(e.GetPosition(fieldHost));
        };
        fieldHost.MouseMove += (_, e) =>
        {
            if (fieldHost.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) PickField(e.GetPosition(fieldHost));
        };
        fieldHost.MouseLeftButtonUp += (_, _) => fieldHost.ReleaseMouseCapture();

        var hueHost = Area(FieldWidth, HueHeight, HueHeight / 2, _hueFill, null, _hueLayer, _hueThumb);
        hueHost.Margin = new Thickness(0, 6, 0, 0);
        hueHost.Cursor = Cursors.Hand;
        hueHost.MouseLeftButtonDown += (_, e) =>
        {
            hueHost.CaptureMouse();
            PickHue(e.GetPosition(hueHost));
        };
        hueHost.MouseMove += (_, e) =>
        {
            if (hueHost.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) PickHue(e.GetPosition(hueHost));
        };
        hueHost.MouseLeftButtonUp += (_, _) => hueHost.ReleaseMouseCapture();

        var hexRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hexLabel.VerticalAlignment = VerticalAlignment.Center;
        _hexLabel.Margin = new Thickness(0, 0, 10, 0);
        hexRow.Children.Add(_hexLabel);
        Grid.SetColumn(_hex, 1);
        hexRow.Children.Add(_hex);

        var swatches = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (id, _) in Theme.Accents)
        {
            if (id == "system") continue; // not a fixed colour: it follows Windows
            swatches.Children.Add(Swatch(id, Theme.AccentFor(id)));
        }

        var previewRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _previewLabel.VerticalAlignment = VerticalAlignment.Center;
        _previewLabel.Margin = new Thickness(0, 0, 10, 0);
        previewRow.Children.Add(_previewLabel);
        Grid.SetColumn(_preview, 1);
        _preview.HorizontalAlignment = HorizontalAlignment.Left;
        previewRow.Children.Add(_preview);

        var ok = AccentButton(Loc.T("common.ok"), (_, _) =>
        {
            Selected = _colour;
            DialogResult = true;
        });
        ok.IsDefault = true;
        var cancel = PlainButton(Loc.T("common.cancel"), (_, _) => DialogResult = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        _ok = ok;
        _cancel = cancel;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18), Width = FieldWidth };
        root.Children.Add(_desc);
        root.Children.Add(fieldHost);
        root.Children.Add(_hueLabel);
        root.Children.Add(hueHost);
        root.Children.Add(hexRow);
        root.Children.Add(_problem);
        root.Children.Add(_presetsLabel);
        root.Children.Add(swatches);
        root.Children.Add(previewRow);
        root.Children.Add(buttons);
        Content = root;
        _hueLabel.Margin = new Thickness(0, 12, 0, 0);
        _presetsLabel.Margin = new Thickness(0, 12, 0, 0);

        SyncUi(updateHex: true);
    }

    /// <summary>
    /// Shows the picker over <paramref name="owner"/>. Returns null when the user cancels.
    /// </summary>
    public static Color? Pick(Window? owner, Color initial)
    {
        var dialog = new ColorPickerWindow(initial);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    protected override void OnLanguageReloaded()
    {
        Title = Loc.T("accent.custom.title");
        _desc.Text = Loc.T("accent.custom.desc");
        _hueLabel.Text = Loc.T("accent.custom.hue");
        _hexLabel.Text = Loc.T("accent.custom.hex");
        _presetsLabel.Text = Loc.T("accent.custom.presets");
        _previewLabel.Text = Loc.T("accent.custom.preview");
        _problem.Text = Loc.T("accent.custom.invalid");
        if (_ok is not null) _ok.Content = Loc.T("common.ok");
        if (_cancel is not null) _cancel.Content = Loc.T("common.cancel");
    }

    protected override void OnPaletteChanged()
    {
        _problem.Foreground = Theme.Brush(Theme.Current.Danger);
        _preview.BorderBrush = Theme.Brush(Theme.Current.Stroke);
        SyncUi(updateHex: false);
    }

    // ------------------------------------------------------------------ picking

    void PickField(Point at)
    {
        _saturation = Math.Clamp(at.X / FieldWidth, 0, 1);
        _brightness = 1 - Math.Clamp(at.Y / FieldHeight, 0, 1);
        _colour = FromHsv(_hue, _saturation, _brightness);
        SyncUi(updateHex: true);
    }

    void PickHue(Point at)
    {
        _hue = Math.Clamp(at.X / FieldWidth, 0, 1) * 360;
        _colour = FromHsv(_hue, _saturation, _brightness);
        SyncUi(updateHex: true);
    }

    void OnHexTyped()
    {
        if (_syncing) return;
        if (!TryParseHex(_hex.Text, out var colour))
        {
            // Not a colour yet: say so and keep the old one rather than jumping about.
            _problem.Visibility = Visibility.Visible;
            if (_ok is not null) _ok.IsEnabled = false;
            return;
        }
        _problem.Visibility = Visibility.Collapsed;
        if (_ok is not null) _ok.IsEnabled = true;
        _colour = colour;
        (_hue, _saturation, _brightness) = ToHsv(colour);
        SyncUi(updateHex: false);
    }

    /// <summary>Rewrites a half-typed value in the canonical form once the field loses focus.</summary>
    void NormaliseHex()
    {
        if (TryParseHex(_hex.Text, out _)) SyncUi(updateHex: true);
    }

    void SyncUi(bool updateHex)
    {
        _fieldFill.GradientStops[1].Color = FromHsv(_hue, 1, 1);
        Canvas.SetLeft(_fieldThumb, _saturation * FieldWidth - ThumbRadius);
        Canvas.SetTop(_fieldThumb, (1 - _brightness) * FieldHeight - ThumbRadius);
        Canvas.SetLeft(_hueThumb, _hue / 360 * FieldWidth - 2.5);
        _preview.Background = Theme.Brush(_colour);
        _previewText.Foreground = Theme.Brush(Theme.On(_colour));
        if (!updateHex) return;
        _syncing = true;
        _hex.Text = Hex(_colour);
        _syncing = false;
    }

    Border Swatch(string id, Color colour)
    {
        var dot = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Brush(colour),
            BorderBrush = Theme.Brush(Theme.Current.Stroke),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("accent." + id),
        };
        dot.MouseLeftButtonUp += (_, _) =>
        {
            _colour = colour;
            (_hue, _saturation, _brightness) = ToHsv(colour);
            SyncUi(updateHex: true);
        };
        return dot;
    }

    // ------------------------------------------------------------------ small builders

    static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12.5,
        Opacity = 0.72,
    };

    /// <summary>A colour field: one or two gradients with a thumb on top, over a transparent hit area.</summary>
    static Grid Area(double width, double height, double radius, Brush fill, Brush? shade,
        Canvas layer, FrameworkElement thumb)
    {
        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
        };
        host.Children.Add(new Rectangle { Fill = fill, RadiusX = radius, RadiusY = radius });
        if (shade is not null) host.Children.Add(new Rectangle { Fill = shade, RadiusX = radius, RadiusY = radius });
        layer.IsHitTestVisible = false;
        layer.Children.Add(thumb);
        host.Children.Add(layer);
        return host;
    }

    static LinearGradientBrush FieldFill()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop(Colors.White, 0));
        brush.GradientStops.Add(new GradientStop(Colors.White, 1));
        return brush;
    }

    static LinearGradientBrush HueFill()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        var stops = new (double Offset, Color Colour)[]
        {
            (0, Colors.Red), (1.0 / 6, Colors.Yellow), (2.0 / 6, Colors.Lime),
            (0.5, Colors.Cyan), (4.0 / 6, Colors.Blue), (5.0 / 6, Colors.Magenta), (1, Colors.Red),
        };
        foreach (var (offset, colour) in stops) brush.GradientStops.Add(new GradientStop(colour, offset));
        return brush;
    }

    // ------------------------------------------------------------------ colour maths

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static bool TryParseHex(string? text, out Color colour)
    {
        colour = Colors.Transparent;
        var digits = (text ?? "").Trim().TrimStart('#');
        if (digits.Length != 6) return false;
        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        colour = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Hue in degrees, saturation and brightness from 0 to 1.</summary>
    static (double Hue, double Saturation, double Brightness) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? (g - b) / delta + (g < b ? 6 : 0)
                : max == g ? (b - r) / delta + 2
                : (r - g) / delta + 4;
            hue *= 60;
        }
        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    static Color FromHsv(double hue, double saturation, double brightness)
    {
        hue = (hue % 360 + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        brightness = Math.Clamp(brightness, 0, 1);
        var chroma = brightness * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = brightness - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
