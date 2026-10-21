using System.Text.Json.Serialization;

namespace DragDone.Core;

/// <summary>
/// The portable half of the settings: everything that decides how DragDone looks and
/// behaves while dragging, and nothing that is about this machine's files.
/// </summary>
public sealed class Look
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    public string ThemePreset { get; set; } = "windows";
    public ColorMode Theme { get; set; } = ColorMode.System;
    public string Accent { get; set; } = "teal";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccentHex { get; set; }
    public double AccentStrength { get; set; } = 1.0;
    public MenuLayout Layout { get; set; } = MenuLayout.Auto;
    public Density Density { get; set; } = Density.Roomy;
    public bool Translucent { get; set; } = true;
    public bool BackdropBlur { get; set; } = true;
    public double CornerRatio { get; set; } = 0.10;
    public double Gap { get; set; } = 4;
    public double FrameThickness { get; set; } = 1.2;
    public bool RotatedLabels { get; set; }
    public bool ShowTabBar { get; set; } = true;
    public bool ShowHints { get; set; } = true;
    public LabelStyle Labels { get; set; } = LabelStyle.Auto;
    public double MenuDiameter { get; set; } = 352;
    public double Contrast { get; set; } = 1.0;
    public double HoverGrow { get; set; } = 1.0;

    [JsonIgnore]
    public string Summary => $"{ThemePreset}/{Layout}/{Density}";

    public static Look From(Settings s) => new()
    {
        ThemePreset = s.ThemePreset,
        Theme = s.Theme,
        Accent = s.Accent,
        AccentHex = s.AccentHex,
        AccentStrength = s.AccentStrength,
        Layout = s.Layout,
        Density = s.Density,
        Translucent = s.Translucent,
        BackdropBlur = s.BackdropBlur,
        CornerRatio = s.CornerRatio,
        Gap = s.Gap,
        FrameThickness = s.FrameThickness,
        RotatedLabels = s.RotatedLabels,
        ShowTabBar = s.ShowTabBar,
        ShowHints = s.ShowHints,
        Labels = s.Labels,
        MenuDiameter = s.MenuDiameter,
        Contrast = s.Contrast,
        HoverGrow = s.HoverGrow,
    };

    public void CopyTo(Settings s)
    {
        s.ThemePreset = ThemePreset;
        s.Theme = Theme;
        s.Accent = Accent;
        s.AccentHex = AccentHex;
        s.AccentStrength = AccentStrength;
        s.Layout = Layout;
        s.Density = Density;
        s.Translucent = Translucent;
        s.BackdropBlur = BackdropBlur;
        s.CornerRatio = CornerRatio;
        s.Gap = Gap;
        s.FrameThickness = FrameThickness;
        s.RotatedLabels = RotatedLabels;
        s.ShowTabBar = ShowTabBar;
        s.ShowHints = ShowHints;
        s.Labels = Labels;
        s.MenuDiameter = MenuDiameter;
        s.Contrast = Contrast;
        s.HoverGrow = HoverGrow;
    }
}
