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
/// <remarks>
/// <see cref="Office"/> stays the all-or-nothing answer older callers pass (any Office at all).
/// The per-app flags refine it; null means "not asked", and then the app follows
/// <see cref="Office"/>, so <c>new Capabilities(false, false, folders)</c> keeps meaning
/// "no Office anywhere".
/// </remarks>
public sealed record Capabilities(
    bool Ffmpeg,
    bool Office,
    IReadOnlyList<string> RecentFolders,
    bool? Word = null,
    bool? Excel = null,
    bool? PowerPoint = null,
    bool? Visio = null,
    bool? Publisher = null)
{
    public static Capabilities All { get; } =
        new(true, true, Array.Empty<string>(), Word: true, Excel: true, PowerPoint: true, Visio: true, Publisher: true);

    /// <summary>No external tool at all: only the built-in, dependency-free actions stay enabled.</summary>
    public static Capabilities None { get; } =
        new(false, false, Array.Empty<string>(), Word: false, Excel: false, PowerPoint: false, Visio: false, Publisher: false);

    /// <summary>Whether the application that owns <paramref name="family"/> can be automated.</summary>
    public bool Can(OfficeFamily family) => family switch
    {
        OfficeFamily.Word => Word ?? Office,
        OfficeFamily.Excel => Excel ?? Office,
        OfficeFamily.PowerPoint => PowerPoint ?? Office,
        OfficeFamily.Visio => Visio ?? Office,
        OfficeFamily.Publisher => Publisher ?? Office,
        _ => false,
    };

    /// <summary>Catalogue key naming the application a disabled sector is waiting for.</summary>
    public static string MissingKey(OfficeFamily family) => family switch
    {
        OfficeFamily.Word => "reason.needsWord",
        OfficeFamily.Excel => "reason.needsExcel",
        OfficeFamily.PowerPoint => "reason.needsPowerPoint",
        OfficeFamily.Visio => "reason.needsVisio",
        OfficeFamily.Publisher => "reason.needsPublisher",
        _ => "reason.needsOffice",
    };
}
