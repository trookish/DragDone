#pragma warning disable WPF0001 // ThemeMode (Fluent) is marked experimental in .NET 10 but supported.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>
/// Base for DragDone's ordinary windows: WPF Fluent theme (Windows 11 controls, Mica where
/// available), dark title bar, app icon, and the current language's flow direction.
/// </summary>
public class FluentWindow : Window
{
    public static readonly ImageSource AppIcon = LoadIcon();

    static ImageSource LoadIcon()
    {
        var img = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/DragDone.ico", UriKind.Absolute));
        img.Freeze();
        return img;
    }

    public FluentWindow()
    {
        Icon = AppIcon;
        FlowDirection = Loc.Flow;
        FontFamily = Theme.UiFont;
        FontSize = 13;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => SystemChrome();
        Theme.Changed += OnThemeChanged;
        // Theme is static: without this every window ever opened would be kept alive by it.
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    protected bool CloseOnEscape { get; set; } = true;

    void OnThemeChanged()
    {
        // The chrome change re-templates the window, which throws away transient view state
        // such as a scroll position. A window that cares parks it here, before that happens.
        OnThemeChanging();
        SystemChrome();
        OnPaletteChanged();
    }

    /// <summary>Called before the new palette is applied, while the old layout is still intact.</summary>
    protected virtual void OnThemeChanging() { }

    /// <summary>
    /// The interface language changed while this window was open: the text direction may have
    /// flipped, so the window and its content have to be laid out again.
    /// </summary>
    protected virtual void OnLanguageChanged()
    {
        FlowDirection = Loc.Flow;
        OnLanguageReloaded();
    }

    /// <summary>Override to rebuild content that was written in the previous language.</summary>
    protected virtual void OnLanguageReloaded() { }

    /// <summary>Override to restyle programmatic content when the palette changes.</summary>
    protected virtual void OnPaletteChanged() { }

    /// <summary>Title bar, controls and window material follow the current palette.</summary>
    void SystemChrome()
    {
        ThemeMode = Theme.Current.IsDark ? ThemeMode.Dark : ThemeMode.Light;
        if (new WindowInteropHelper(this).Handle is var hwnd && hwnd != IntPtr.Zero && IsLoaded) Apply(hwnd);
        else SourceInitialized += (_, _) => Apply(new WindowInteropHelper(this).Handle);
    }

    void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var dark = Theme.Current.IsDark ? 1 : 0;
        // 20 is the modern attribute; 19 is what Windows 10 1809-1909 reads.
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));

        // Mica on Windows 11 22000+, acrylic on 17763+; older builds keep an opaque surface.
        var build = Environment.OSVersion.Version.Build;
        var backdrop = Theme.Current.IsDark && Theme.Current.Translucent ? 2 : 3;
        if (build >= 22523) DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
        else if (build >= 22000) DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

        if (Theme.Current.Translucent)
        {
            var corner = 2; // round
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ------------------------------------------------------------------ small builders

    public static TextBlock Heading(string text, double size = 20) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        FontFamily = Theme.DisplayFont,
        Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock Muted(string text, double size = 12.5) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = 0.72,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>An ⓘ glyph whose tooltip explains a section (shown on hover).</summary>
    public static FrameworkElement InfoDot(string explanation)
    {
        var t = new TextBlock
        {
            Text = "\uE946",
            FontFamily = Theme.IconFont,
            FontSize = 14,
            Opacity = 0.65,
            Margin = new Thickness(8, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,
            ToolTip = new ToolTip
            {
                Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
                FlowDirection = Loc.Flow,
            },
        };
        ToolTipService.SetInitialShowDelay(t, 150);
        ToolTipService.SetShowDuration(t, 30000);
        return t;
    }

    /// <summary>Rounded card used to group settings, like Windows 11 Settings.</summary>
    public static Border Card(UIElement content, Thickness? margin = null) => new()
    {
        Child = content,
        Padding = new Thickness(16, 12, 16, 14),
        Margin = margin ?? new Thickness(0, 0, 0, 8),
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
    };

    /// <summary>A key cap like <c>Shift</c>, always laid out left-to-right.</summary>
    public static Border KeyCap(string key) => new()
    {
        Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight },
        Padding = new Thickness(7, 2, 7, 3),
        Margin = new Thickness(0, 0, 8, 0),
        CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(1, 1, 1, 2),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button AccentButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, MinWidth = 96, Padding = new Thickness(14, 5, 14, 6) };
        b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        b.Click += onClick;
        return b;
    }

    public static Button PlainButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, MinWidth = 96, Padding = new Thickness(14, 5, 14, 6) };
        b.Click += onClick;
        return b;
    }
}
