using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DragDone.Localization;

/// <summary>
/// String catalogue. Arabic (formal MSA) is the default; English is the second language.
/// A missing key renders as the key itself so gaps are visible, never silently English.
/// Digits: sizes and counts use Arabic-Indic digits in Arabic; format names stay Latin.
/// </summary>
public static class Loc
{
    static readonly Dictionary<string, Dictionary<string, string>> Catalogs = new();
    static string _lang = "ar";

    public static event Action? LanguageChanged;

    public static string Language => _lang;
    public static bool IsRtl => _lang == "ar";
    public static System.Windows.FlowDirection Flow =>
        IsRtl ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;

    public static IReadOnlyList<string> Languages { get; } = new[] { "ar", "en" };

    static Loc()
    {
        foreach (var lang in Languages) Catalogs[lang] = LoadCatalog(lang);
    }

    public static IReadOnlyDictionary<string, string> Catalog(string lang) => Catalogs[lang];

    static Dictionary<string, string> LoadCatalog(string lang)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"DragDone.Localization.{lang}.json")
                           ?? throw new InvalidOperationException($"Missing catalogue {lang}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }

    public static void Set(string lang)
    {
        if (!Catalogs.ContainsKey(lang)) lang = "ar";
        if (_lang == lang) return;
        _lang = lang;
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// The language the operating system is showing, as one of ours, or null when it is a
    /// language we do not ship.
    ///
    /// The display language is what the user reads Windows in, so it is the right thing to
    /// follow. Three sources are consulted, most reliable first:
    /// <list type="number">
    /// <item>the preferred UI languages Windows keeps in the registry, which is what the user
    /// picks in Settings and what changes the moment they pick it;</item>
    /// <item>the live system call, which is right but is only fixed when the user signs in;</item>
    /// <item>the thread's UI culture, as a last resort.</item>
    /// </list>
    /// </summary>
    public static string? SystemLanguage()
    {
        foreach (var tag in Candidates())
        {
            var match = Match(tag);
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>Language tags to try, most reliable first.</summary>
    static IEnumerable<string> Candidates()
    {
        foreach (var tag in PreferredUiLanguages()) yield return tag;
        foreach (var tag in FromLangId(Native.Win32.GetUserDefaultUILanguage())) yield return tag;
        var ui = CultureInfo.CurrentUICulture;
        yield return ui.Name;
        if (!string.IsNullOrEmpty(ui.Parent?.Name) && ui.Parent.Name != ui.Name) yield return ui.Parent.Name;
    }

    /// <summary>
    /// The languages Windows is set to show, most preferred first. This is the list the user
    /// edits in Windows' own language settings, so it is what the interface should follow.
    /// </summary>
    static IEnumerable<string> PreferredUiLanguages()
    {
        string[]? names;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("PreferredUILanguages") is string[] list) names = list;
            else if (key?.GetValue("PreferredUILanguagesPending") is string[] pending) names = pending;
            else names = null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            yield break;
        }
        if (names is null) yield break;
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name)) yield return name.Trim();
        }
    }

    /// <summary>
    /// Turns a Windows LANGID into a language tag. Only the primary language matters here:
    /// DragDone ships one variant per language, and the region never changes which catalogue
    /// is used. (The sublanguage in a LANGID is a per-language enum, not a BCP-47 region.)
    /// </summary>
    static IEnumerable<string> FromLangId(int langId)
    {
        if (langId == 0) yield break;
        var primary = langId & 0x3FF;
        var name = primary switch
        {
            0x01 => "ar",
            0x02 => "bg",
            0x04 => "zh",
            0x07 => "de",
            0x09 => "en",
            0x0A => "es",
            0x0C => "fr",
            0x10 => "it",
            0x12 => "ko",
            0x13 => "nl",
            0x14 => "nb",
            0x15 => "pl",
            0x16 => "pt",
            0x1D => "sv",
            0x1F => "tr",
            0x22 => "uk",
            0x24 => "sl",
            0x29 => "fa",
            0x2E => "vi",
            0x2F => "et",
            0x36 => "af",
            0x3E => "ms",
            _ => null,
        };
        if (name is not null) yield return name;
    }

    /// <summary>Maps a language tag onto one of our languages, or null when there is no match.</summary>
    static string? Match(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var normalised = tag.Replace('_', '-');
        foreach (var lang in Languages)
        {
            if (string.Equals(normalised, lang, StringComparison.OrdinalIgnoreCase)) return lang;
            if (normalised.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase)) return lang;
        }
        return null;
    }

    /// <summary>
    /// The language to actually use: the fixed choice when the user made one, otherwise the
    /// operating system's language, and Arabic when the system speaks a language we do not.
    /// </summary>
    public static string Resolve(Core.LanguageMode mode)
    {
        return mode switch
        {
            Core.LanguageMode.Arabic => "ar",
            Core.LanguageMode.English => "en",
            _ => SystemLanguage() ?? "ar",
        };
    }

    public static string T(string key) => T(_lang, key);

    public static string T(string lang, string key) =>
        Catalogs.TryGetValue(lang, out var cat) && cat.TryGetValue(key, out var v) ? v : key;

    /// <summary>Format with named placeholders: <c>{name}</c>.</summary>
    public static string F(string key, params (string Name, object Value)[] args)
    {
        var s = T(key);
        foreach (var (name, value) in args)
        {
            var text = value switch
            {
                int i => Num(i),
                long l => Num(l),
                _ => value?.ToString() ?? "",
            };
            s = s.Replace("{" + name + "}", text);
        }
        return s;
    }

    public static string Num(long n) => Digits(n.ToString(CultureInfo.InvariantCulture));

    /// <summary>Latin → Arabic-Indic digits when the UI is Arabic.</summary>
    public static string Digits(string s)
    {
        if (!IsRtl) return s;
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '0' and <= '9') chars[i] = (char)('\u0660' + (chars[i] - '0'));
            else if (chars[i] == '.') chars[i] = '\u066B'; // Arabic decimal separator
        }
        return new string(chars);
    }

    public static string Size(long bytes)
    {
        string[] keys = { "unit.b", "unit.kb", "unit.mb", "unit.gb", "unit.tb" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < keys.Length - 1)
        {
            v /= 1024;
            u++;
        }
        var number = u == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        return Digits(number) + " " + T(keys[u]);
    }

    /// <summary>
    /// The kind of a drag, as the pill above the ring names it. Office documents are named after
    /// the application that owns them ("Word document", "Spreadsheet", "Presentation") because
    /// "Document" tells the user nothing when a .docx and a .txt behave so differently.
    /// </summary>
    public static string KindName(Core.FileKind kind, int count, Core.OfficeFamily office = Core.OfficeFamily.None)
    {
        var name = OfficeName(office) ?? kind.ToString().ToLowerInvariant();
        return T(count > 1 ? $"kind.{name}.plural" : $"kind.{name}");
    }

    static string? OfficeName(Core.OfficeFamily family) => family switch
    {
        Core.OfficeFamily.Word => "worddocument",
        Core.OfficeFamily.Excel => "spreadsheet",
        Core.OfficeFamily.PowerPoint => "presentation",
        Core.OfficeFamily.Visio => "drawing",
        Core.OfficeFamily.Publisher => "publication",
        _ => null,
    };
}
