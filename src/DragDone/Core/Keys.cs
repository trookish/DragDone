namespace DragDone.Core;

/// <summary>
/// The keys that can be bound to a menu. Kept as plain strings in the settings file so a
/// hand-edited or shared file stays readable.
/// </summary>
public static class Keys
{
    public const string None = "";

    /// <summary>
    /// Keys that can open a menu. Mouse buttons are deliberately absent: Windows cancels a
    /// file drag the moment the middle or right button is pressed, so Explorer never hands over
    /// the dragged files and the menu would open with nothing to act on. Offering them would be a
    /// dead end, so they are rejected instead.
    /// </summary>
    public static readonly string[] All = { "Shift", "Ctrl", "Alt", "Win" };

    /// <summary>Keys that are known but cannot be used to open a menu. See <see cref="All"/>.</summary>
    public static readonly string[] Unusable = { "Middle", "Right" };

    public static string Sanitize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return None;
        var match = All.FirstOrDefault(k => string.Equals(k, key.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? None;
    }

    public static bool IsMouse(string key) => key is "Middle" or "Right";

    public static string ToKeyId(string key) => "key." + key.ToLowerInvariant() + (key.Length == 0 ? "none" : "");

    /// <summary>Readable label for a bound key, in the current interface language.</summary>
    public static string Label(string key) => key.Length == 0 ? Localization.Loc.T("key.none") : Localization.Loc.T(ToKeyId(key));
}

/// <summary>Reads the key bindings out of the settings, so nothing hard-codes Shift/Ctrl/Alt.</summary>
public static class BoundKeys
{
    public static string TabKey(Settings s, MenuTab tab) => tab switch
    {
        MenuTab.Convert => s.ConvertKey,
        MenuTab.Tools => s.ToolsKey,
        _ => s.ActionsKey,
    };

    public static void Set(Settings s, MenuTab tab, string key)
    {
        switch (tab)
        {
            case MenuTab.Convert: s.ConvertKey = key; break;
            case MenuTab.Tools: s.ToolsKey = key; break;
            default: s.ActionsKey = key; break;
        }
    }

    /// <summary>True when the combination leaves at least one tab reachable.</summary>
    public static bool AnyBound(Settings s) => s.ConvertKey.Length + s.ToolsKey.Length + s.ActionsKey.Length > 0;
}
