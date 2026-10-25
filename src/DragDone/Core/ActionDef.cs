using DragDone.Localization;

namespace DragDone.Core;

public enum MenuTab
{
    Convert,
    Tools,
    Actions,
}

public enum ActionStyle
{
    /// <summary>Large bold format name (PNG, MP4) as in the convert ring.</summary>
    Format,

    /// <summary>Glyph above a short label.</summary>
    Icon,
}

/// <summary>One sector of the radial menu. Immutable; built per drag by <see cref="ActionCatalog"/>.</summary>
public sealed record ActionDef
{
    public required string Id { get; init; }
    public string? LabelKey { get; init; }

    /// <summary>Untranslated label (format names, folder names).</summary>
    public string? Literal { get; init; }

    public string Glyph { get; init; } = "";
    public ActionStyle Style { get; init; } = ActionStyle.Icon;

    /// <summary>Set for entries whose glyph is the whole message (the overflow ellipsis).</summary>
    public bool GlyphOnly { get; init; }
    public IReadOnlyList<ActionDef> Children { get; init; } = Array.Empty<ActionDef>();

    /// <summary>Child run when the drop lands on this parent instead of opening its ring.</summary>
    public string? DefaultChildId { get; init; }

    /// <summary>Null when enabled; otherwise the catalogue key that explains why not.</summary>
    public string? DisabledReasonKey { get; init; }

    /// <summary>Optional hint shown in the hub while hovering (catalogue key).</summary>
    public string? HintKey { get; init; }

    public bool Enabled => DisabledReasonKey is null;
    public bool HasChildren => Children.Count > 0;

    public string Label => Literal ?? (LabelKey is null ? Id : Loc.T(LabelKey));

    /// <summary>Disables this sector and everything under it, so no child stays reachable.</summary>
    public ActionDef Disabled(string reasonKey) => this with
    {
        DisabledReasonKey = reasonKey,
        Children = Children.Select(c => c.Disabled(reasonKey)).ToList(),
    };
}

/// <summary>What the machine can do right now; decides which sectors are enabled.</summary>
public sealed record Capabilities(bool Ffmpeg, bool Office, IReadOnlyList<string> RecentFolders, IReadOnlyList<CustomAction>? Custom = null)
{
    public IReadOnlyList<CustomAction> CustomActions => Custom ?? Array.Empty<CustomAction>();

    public static Capabilities All { get; } = new(true, true, Array.Empty<string>());
}
