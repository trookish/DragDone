using System.Text.Json;
using System.Text.Json.Serialization;

namespace DragDone.Core;

public enum TriggerMode
{
    /// <summary>Shift / Ctrl / Alt pressed during a drag opens the menu.</summary>
    Modifier,

    /// <summary>Also opens the menu when the drag rests still for a moment.</summary>
    ModifierOrPause,
}

public enum SourceScope
{
    /// <summary>Only drags that start on the Desktop or in File Explorer.</summary>
    Shell,

    /// <summary>Any application that drags files.</summary>
    Anywhere,
}

/// <summary>Light, dark, or whatever Windows is using right now.</summary>
public enum ColorMode
{
    System,
    Light,
    Dark,
}

public enum MenuLayout
{
    /// <summary>Thin ring with the labels around it; the middle stays open.</summary>
    Donut,

    /// <summary>Classic filled wheel.</summary>
    Pie,

    /// <summary>Circular badges floating in a circle around an empty middle.</summary>
    Orbit,

    /// <summary>Donut for a few sectors, Pie once the ring gets crowded.</summary>
    Auto,

    /// <summary>Rounded petals around the hub, like a flower.</summary>
    Petals,

    /// <summary>Hexagonal cells floating in a circle.</summary>
    Honeycomb,

    /// <summary>Rounded square tiles floating in a circle.</summary>
    Tiles,

    /// <summary>Diamond badges floating in a circle.</summary>
    Diamonds,

    /// <summary>A half ring that opens above the cursor.</summary>
    Fan,
}

public enum LabelStyle
{
    /// <summary>Icon with the name beside it, unless the sector is too narrow.</summary>
    Auto,

    Icons,

    Labels,

    /// <summary>Name only, for dense rings.</summary>
    Text,
}

public enum Density
{
    Compact,
    Roomy,
    Large,
}

public sealed class Settings
{
    // ---- general
    /// <summary>Which language the interface uses. <see cref="LanguageMode.System"/> follows Windows.</summary>
    public LanguageMode LanguageMode { get; set; } = LanguageMode.System;

    /// <summary>
    /// The resolved language code ("ar" or "en"). Kept so an older settings file, or one written
    /// by hand, still pins a language; it is ignored when <see cref="LanguageMode"/> is not System.
    /// </summary>
    public string Language { get; set; } = "";
    public TriggerMode Trigger { get; set; } = TriggerMode.Modifier;
    public SourceScope Scope { get; set; } = SourceScope.Shell;
    public bool StartWithWindows { get; set; } = true;
    public bool Paused { get; set; }
    public bool ShowWelcome { get; set; } = true;

    /// <summary>
    /// Which corner the settings window's floating live preview docks to. Drag the preview
    /// to another part of the window and it re-docks on the nearest corner.
    /// </summary>
    public PreviewCorner PreviewDock { get; set; } = PreviewCorner.BottomRight;

    // ---- what each key opens ("" = that key does nothing)
    public string ConvertKey { get; set; } = "Shift";
    public string ToolsKey { get; set; } = "Ctrl";
    public string ActionsKey { get; set; } = "Alt";

    // ---- appearance
    public string ThemePreset { get; set; } = "windows";
    public ColorMode Theme { get; set; } = ColorMode.System;
    public string Accent { get; set; } = "teal";
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

    // ---- motion
    public OpenAnimation Animation { get; set; } = OpenAnimation.Pop;
    public HoverEffect Hover { get; set; } = HoverEffect.Grow;

    /// <summary>Length of the opening animation; 0 turns it off.</summary>
    public int AnimationMs { get; set; } = 240;

    /// <summary>How long a sector takes to settle into its hover state; 0 is instant.</summary>
    public int HoverMs { get; set; } = 140;

    /// <summary>Replays a shorter version of the opening animation on tab and submenu changes.</summary>
    public bool AnimateRingChanges { get; set; } = true;

    /// <summary>Turns motion off when Windows' own "Animation effects" setting is off.</summary>
    public bool FollowSystemMotion { get; set; } = true;

    /// <summary>How long a drag must rest before the menu opens, when that trigger is on.</summary>
    public int DwellMs { get; set; } = 650;

    // ---- jobs
    public int JpegQuality { get; set; } = 90;
    public int ResultSeconds { get; set; } = 4;
    public string? FfmpegPath { get; set; }
    public string? OutputFolder { get; set; }
    public List<string> RecentFolders { get; set; } = new();

    // ---- customisation
    /// <summary>Programs the user added to the rings; see <see cref="CustomAction"/>.</summary>
    public List<CustomAction> CustomActions { get; set; } = new();

    /// <summary>Extensions the user assigned to a kind, so unknown files get the right rings.</summary>
    public Dictionary<string, FileKind> ExtraTypes { get; set; } = new();

    /// <summary>Bumped when a release needs to migrate the file.</summary>
    public int Version { get; set; } = 2;

    [JsonIgnore]
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragDone");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Folder, "settings.json");

    [JsonIgnore]
    public static string ThemePath => Path.Combine(Folder, "theme.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var read = JsonSerializer.Deserialize<Settings>(text, Json);
                if (read is not null) s = read;
                // A file written before DragDone followed Windows carries a plain language code
                // and no language mode. English there was a deliberate choice; Arabic was only
                // the old default, and must not pin the language on a machine in English.
                if (read is not null && !Mentions(text, "LanguageMode"))
                    s.LanguageMode = read.Language == "en" ? LanguageMode.English : LanguageMode.System;
            }
        }
        // A settings file DragDone may not read (a locked or ACL-protected profile) must not stop the
        // app from starting: it runs on defaults instead.
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }

        // A theme file overrides the look: it is the copy people share and hand-edit, so it wins
        // over the copy that happens to be inside settings.json.
        try
        {
            if (File.Exists(ThemePath) && JsonSerializer.Deserialize<Look>(File.ReadAllText(ThemePath), Json) is { } look)
                look.CopyTo(s);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }

        return s.Normalized();
    }

    /// <summary>True when the settings text names the given property, so a migration can tell an
    /// old file from a new one.</summary>
    static bool Mentions(string text, string property)
    {
        var quoted = "\"" + property + "\"";
        var at = text.IndexOf(quoted, StringComparison.Ordinal);
        return at >= 0;
    }

    /// <summary>
    /// Writes two files: machine settings, and the portable look. Keeping them apart is what
    /// makes a theme a single file that can be copied between machines.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        WriteAtomic(FilePath, JsonSerializer.Serialize(this, Json));
        WriteAtomic(ThemePath, JsonSerializer.Serialize(Look.From(this), Json));
    }

    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Writes just the look, so a theme can be shared, backed up, or hand-edited.</summary>
    public void ExportTheme(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ExportThemeJson());
    }

    public string ExportThemeJson()
    {
        var look = Look.From(this);
        look.Name = Localization.Loc.T("theme." + ThemePreset);
        return JsonSerializer.Serialize(look, Json);
    }

    /// <summary>Applies a theme file. Returns false when the text is not a theme.</summary>
    public bool ImportThemeJson(string json)
    {
        Look? look;
        try { look = JsonSerializer.Deserialize<Look>(json, Json); }
        catch (JsonException) { return false; }
        if (look is null) return false;
        look.CopyTo(this);
        Normalized();
        return true;
    }

    Settings Normalized()
    {
        if (!Enum.IsDefined(LanguageMode)) LanguageMode = LanguageMode.System;
        if (!Enum.IsDefined(PreviewDock)) PreviewDock = PreviewCorner.BottomRight;
        if (Language is not ("ar" or "en")) Language = "";
        JpegQuality = Math.Clamp(JpegQuality, 40, 100);
        ResultSeconds = Math.Clamp(ResultSeconds, 2, 15);
        DwellMs = Math.Clamp(DwellMs, 80, 900);
        MenuDiameter = Math.Clamp(MenuDiameter, 240, 520);
        Gap = Math.Clamp(Gap, 0, 16);
        FrameThickness = Math.Clamp(FrameThickness, 0, 4);
        Contrast = Math.Clamp(Contrast, 0.6, 1.6);
        AccentStrength = Math.Clamp(AccentStrength, 0.2, 1.6);
        HoverGrow = Math.Clamp(HoverGrow, 0, 2);
        CornerRatio = Math.Clamp(CornerRatio, 0, 0.5);
        if (!Enum.IsDefined(Layout)) Layout = MenuLayout.Auto;
        if (!Enum.IsDefined(Animation)) Animation = OpenAnimation.Pop;
        if (!Enum.IsDefined(Hover)) Hover = HoverEffect.Grow;
        AnimationMs = Math.Clamp(AnimationMs, 0, 800);
        HoverMs = Math.Clamp(HoverMs, 0, 500);
        if (AccentHex is { Length: > 0 } hex)
            AccentHex = hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
        ConvertKey = SanitizeKey(ConvertKey, "Shift");
        ToolsKey = SanitizeKey(ToolsKey, "Ctrl");
        ActionsKey = SanitizeKey(ActionsKey, "Alt");
        RecentFolders = RecentFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        CustomActions = (CustomActions ?? new()).Where(a => a is not null).Take(32).ToList();
        foreach (var a in CustomActions)
        {
            a.Name ??= "";
            a.Types ??= "*";
            a.Program ??= "";
            a.Arguments ??= "";
            a.Output ??= "";
            if (string.IsNullOrWhiteSpace(a.Id) || CustomActions.Count(x => x.Id == a.Id) > 1) a.Id = CustomAction.NewId();
            if (!Enum.IsDefined(a.Tab)) a.Tab = MenuTab.Tools;
        }
        ExtraTypes = (ExtraTypes ?? new())
            .Select(kv => (Ext: FileKinds.NormalizeExtension(kv.Key), kv.Value))
            .Where(kv => kv.Ext is not null && FileKinds.Assignable.Contains(kv.Value))
            .GroupBy(kv => kv.Ext!)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        FileKinds.SetExtra(ExtraTypes);
        Version = 2;
        return this;
    }

    /// <summary>
    /// The language the interface should use right now: the user's fixed choice, or the
    /// operating system's display language when they asked DragDone to follow Windows.
    /// </summary>
    public string ResolvedLanguage() => Localization.Loc.Resolve(LanguageMode);

    /// <summary>
    /// Keeps a saved key if it can actually open a menu, and otherwise falls back to the default.
    /// A hand-edited file may name a mouse button, which Windows cancels the file drag for; leaving
    /// the tab without a key would make it unreachable, so the default is restored instead.
    /// </summary>
    static string SanitizeKey(string? key, string fallback)
    {
        var clean = Keys.Sanitize(key);
        if (clean.Length > 0) return clean;
        // An empty string is a deliberate "this key does nothing" and is kept as it is.
        if (string.IsNullOrWhiteSpace(key)) return Keys.None;
        return fallback;
    }

    /// <remarks>
    /// Called from the job thread while the UI thread may be building a menu from the current
    /// list, so the list is never changed in place: a new one replaces it, and a reader still
    /// holding the old one keeps a consistent copy.
    /// </remarks>
    public void RememberFolder(string folder)
    {
        lock (RecentLock)
        {
            var next = new List<string>(6) { folder };
            next.AddRange(RecentFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
            if (next.Count > 5) next.RemoveRange(5, next.Count - 5);
            RecentFolders = next;
        }
    }

    static readonly object RecentLock = new();
}
