using System.Windows;
using System.Windows.Media;
using DragDone.Core;
using Microsoft.Win32;

namespace DragDone.UI;

/// <summary>
/// The full colour set of one theme. Every surface draws from here, so a new look never
/// needs a change anywhere else.
/// </summary>
public sealed record Palette(
    bool IsDark,
    Color Accent,
    Color WheelFill,
    Color WheelFillAlt,
    Color WheelEdge,
    Color RingTrack,
    Color Text,
    Color TextSoft,
    Color TextDim,
    Color Surface,
    Color SurfaceAlt,
    Color Stroke,
    Color Danger,
    Color Success,
    Color Warning,
    Color Badge,
    bool Translucent);

/// <summary>
/// Builds the palette for the current settings and keeps it in step with Windows
/// (light/dark and the personalisation accent colour).
/// </summary>
public static class Theme
{
    /// <summary>Named accent colours. "system" follows the Windows personalisation colour.</summary>
    public static readonly (string Id, string Hex)[] Accents =
    {
        ("system", ""),
        ("teal", "#2EC4A6"),
        ("blue", "#4C8DFF"),
        ("violet", "#9B7BFF"),
        ("pink", "#F472B6"),
        ("amber", "#F5A524"),
        ("lime", "#86C232"),
        ("rose", "#F45B7A"),
    };

    static Palette? _current;

    // Lazy: the preset table is declared further down, so a static initialiser would read it null.
    public static Palette Current
    {
        get => _current ??= PaletteFor(new Settings(), false);
        private set => _current = value;
    }

    /// <summary>Raised when the palette changes; every surface redraws from it.</summary>
    public static event Action? Changed;

    public static bool IsDark => Current.IsDark;

    public static Color AccentColor => Current.Accent;

    static Settings _settings = new();
    static bool? _systemDark;

    public static void Attach(Settings settings)
    {
        _settings = settings;
        Apply();
    }

    /// <summary>Rebuilds the palette from the settings and tells every surface to redraw.</summary>
    public static void Apply()
    {
        var dark = _settings.Theme switch
        {
            ColorMode.Dark => true,
            ColorMode.Light => false,
            _ => SystemUsesDark(),
        };
        var next = PaletteFor(_settings, dark);
        // The palette alone does not describe the look: layout, density, labels and geometry
        // change without touching a single colour. Compare the whole appearance, then notify.
        var look = Lookprint();
        if (_hasLook && next == Current && look == _lastLook) return;
        Current = next;
        _lastLook = look;
        _hasLook = true;
        Changed?.Invoke();
    }

    static (string, ColorMode, string, string?, double, double, MenuLayout, Density, bool, bool, double, double, double, LabelStyle, bool, bool, double, bool) _lastLook;
    static bool _hasLook;

    static (string, ColorMode, string, string?, double, double, MenuLayout, Density, bool, bool, double, double, double, LabelStyle, bool, bool, double, bool) Lookprint()
    {
        var s = _settings;
        return (s.ThemePreset, s.Theme, s.Accent, s.AccentHex, s.AccentStrength, s.Contrast, s.Layout, s.Density,
            s.Translucent, s.BackdropBlur, s.FrameThickness, s.Gap, s.MenuDiameter, s.Labels, s.RotatedLabels,
            s.ShowTabBar, s.HoverGrow, s.CornerRatio > 0.12);
    }

    /// <summary>
    /// Called on a slow timer: Windows can switch light/dark or change the accent colour while
    /// DragDone sits in the tray, and the ring should follow without a restart.
    /// </summary>
    public static bool SyncWithSystem()
    {
        var dark = SystemUsesDark();
        var changed = dark != _systemDark;
        _systemDark = dark;
        if (_settings.Theme != ColorMode.System || !changed) return false;
        Apply();
        return true;
    }

    public static bool SystemUsesDark()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>The colour Windows uses for accents, or null when it cannot be read.</summary>
    public static Color? SystemAccent()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null) is not int v) return null;
            // The value is an ARGB DWORD: as a signed int it can be negative, so reinterpret the bits.
            var raw = unchecked((uint)v);
            // Stored as 0xAABBGGRR, where the alpha is the transparency Windows applied.
            return Color.FromRgb((byte)((raw >> 16) & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Color AccentFor(string id)
    {
        if (string.Equals(id, "system", StringComparison.OrdinalIgnoreCase)) return SystemAccent() ?? Hex("#2EC4A6");
        var hex = Accents.FirstOrDefault(a => a.Id == id).Hex;
        return string.IsNullOrEmpty(hex) ? Hex("#2EC4A6") : Hex(hex);
    }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Readable text on top of a colour.</summary>
    public static Color On(Color background) => Luminance(background) > 0.55 ? Color.FromRgb(0x10, 0x14, 0x18) : Colors.White;

    // ------------------------------------------------------------------ palettes

    /// <summary>A starting point. Accent, strength and contrast are applied on top.</summary>
    sealed record Preset(string Id, bool Dark, double Tint, double Strength, bool Translucent, double Frame, double Radius, double Gap, double Hover, bool ArcLabels);

    static readonly Preset[] Presets =
    {
        // Follows Windows: the ring borrows the personalisation colour and the window material.
        new("windows", false, 0.07, 1.00, true, 1.2, 0.10, 4, 1.0, true),
        // Acrylic card: softer fill, lighter strokes, more air.
        new("fluent", false, 0.14, 0.95, true, 1.0, 0.14, 6, 1.1, true),
        // Flat light, no transparency: readable over any wallpaper.
        new("paper", false, 0.03, 1.00, false, 1.0, 0.04, 3, 1.0, true),
        // Flat dark, no transparency, thin frame instead of fill.
        new("graphite", true, 0.00, 0.95, false, 1.0, 0.03, 3, 0.9, false),
        // Outlined ring, labels written along the arc.
        new("terminal", true, 0.00, 1.30, false, 0, 0.00, 2, 0.8, true),
        // Bold saturated shapes for a playful desktop.
        new("vivid", true, 0.72, 1.35, true, 0, 0.22, 8, 1.3, true),
        // Accessibility: heavy strokes, maximum contrast, no effects.
        new("contrast", true, 0.00, 1.00, false, 2.4, 0.00, 5, 0, false),
    };

    public static IReadOnlyList<string> PresetIds { get; } = Presets.Select(p => p.Id).ToArray();

    public static Palette PaletteFor(Settings s, bool dark)
    {
        var preset = Presets.FirstOrDefault(p => p.Id == s.ThemePreset) ?? Presets[0];
        var tint = preset.Tint;
        var accent = ResolveAccent(s);
        var strength = preset.Strength * s.AccentStrength;
        var translucent = s.Translucent && preset.Translucent;
        return Build(dark, tint, accent, strength, translucent, s.FrameThickness, s.Contrast);
    }

    static Color ResolveAccent(Settings s)
    {
        if (!string.IsNullOrEmpty(s.AccentHex)) return Hex(s.AccentHex);
        return AccentFor(s.Accent);
    }

    static Palette Build(bool dark, double tint, Color accent, double strength, bool translucent, double frame, double contrast)
    {
        accent = Saturate(accent, strength);
        if (!dark) accent = Darken(accent, 0.12 * strength);

        var alpha = translucent ? (byte)0xE8 : (byte)0xFF;
        var softAlpha = translucent ? (byte)0xD8 : (byte)0xFF;

        var grey = dark ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xF7, 0xF7, 0xF7);
        // Tint is the blend fraction itself: presets pick how much accent reaches the surface.
        var fill = Mix(grey, accent, Math.Clamp(tint, 0, 1));
        var fillAlt = dark ? Lighten(fill, 0.05) : Darken(fill, 0.03);

        var text = contrast > 1.05
            ? (dark ? Colors.White : Colors.Black)
            : dark ? Colors.White : Color.FromRgb(0x18, 0x1C, 0x22);

        var surface = dark ? Color.FromArgb(softAlpha, 0x20, 0x20, 0x20) : Color.FromArgb(softAlpha, 0xFA, 0xFA, 0xFA);
        var stroke = dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00);

        return new Palette(
            dark,
            accent,
            WithAlpha(fill, alpha),
            WithAlpha(fillAlt, alpha),
            WithAlpha(stroke, (byte)Math.Clamp(stroke.A * contrast * (frame > 0 ? 1.2 : 0.55), 0, 255)),
            WithAlpha(accent, (byte)(dark ? 0x33 : 0x2A)),
            WithAlpha(text, 0xFF),
            WithAlpha(text, (byte)Math.Clamp(0xA8 * contrast, 0, 255)),
            WithAlpha(text, (byte)Math.Clamp(0x70 * contrast, 0, 255)),
            surface,
            dark ? Color.FromArgb(0xF2, 0x28, 0x28, 0x28) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF),
            stroke,
            dark ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Color.FromRgb(0xC4, 0x2B, 0x2B),
            dark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x10, 0x7C, 0x41),
            dark ? Color.FromRgb(0xFC, 0xE1, 0x00) : Color.FromRgb(0x9A, 0x67, 0x00),
            dark ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0xD1, 0x34, 0x38),
            translucent);
    }

    static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Pushes a colour towards (or away from) full saturation while keeping its hue.</summary>
    static Color Saturate(Color c, double amount)
    {
        if (Math.Abs(amount - 1) < 0.01) return c;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        double s = max == min ? 0 : l > 0.5 ? (max - min) / (2 - max - min) : (max - min) / (max + min);
        var h = max == min ? 0
            : max == r ? (g - b) / (max - min) + (g < b ? 6 : 0)
            : max == g ? (b - r) / (max - min) + 2
            : (r - g) / (max - min) + 4;
        h /= 6;
        s = Math.Clamp(s * amount, 0, 1);
        if (amount > 1) l = Math.Clamp(l - (amount - 1) * 0.12, 0.2, 0.8);
        return FromHsl(h, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        if (s == 0) return Color.FromRgb((byte)(l * 255), (byte)(l * 255), (byte)(l * 255));
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromRgb(
            (byte)(Hue(p, q, h + 1.0 / 3) * 255),
            (byte)(Hue(p, q, h) * 255),
            (byte)(Hue(p, q, h - 1.0 / 3) * 255));
    }

    static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    // ------------------------------------------------------------------ brushes

    /// <summary>A frozen brush for a live colour, cheap to use every frame.</summary>
    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Accent => Brush(Current.Accent);

    public static SolidColorBrush OnAccent => Brush(On(Current.Accent));

    public static FontFamily UiFont { get; } = new("Segoe UI Variable Text, Segoe UI, Tahoma");
    public static FontFamily DisplayFont { get; } = new("Segoe UI Variable Display, Segoe UI, Tahoma");
    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}
