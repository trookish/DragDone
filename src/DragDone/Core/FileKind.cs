namespace DragDone.Core;

public enum FileKind
{
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Archive,
    Folder,
    Other,
    Mixed,
}

/// <summary>The Office application that converts a document.</summary>
public enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

public static class FileKinds
{
    static readonly Dictionary<string, FileKind> ByExtension = Build();

    static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, params string[] exts)
        {
            foreach (var e in exts) map[e] = kind;
        }

        Add(FileKind.Image, ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff",
            ".webp", ".heic", ".heif", ".avif", ".ico", ".jxr", ".wdp", ".dds");
        Add(FileKind.Video, ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg",
            ".mpeg", ".3gp", ".ts", ".mts", ".m2ts", ".ogv");
        Add(FileKind.Audio, ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".wma",
            ".aiff", ".aif", ".amr", ".alac");
        Add(FileKind.Pdf, ".pdf");
        Add(FileKind.Document, ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".ini",
            ".yaml", ".yml", ".rtf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp",
            ".html", ".htm", ".cs", ".py", ".js", ".ts", ".css");
        Add(FileKind.Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab");
        // .ts is ambiguous (MPEG transport stream vs TypeScript); video wins because of its size profile.
        map[".ts"] = FileKind.Video;
        return map;
    }

    // Extensions the user taught DragDone in Settings; they win over the built-in table.
    static volatile Dictionary<string, FileKind> _extra = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kinds a user may assign an extension to.</summary>
    public static readonly FileKind[] Assignable = { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Document, FileKind.Archive };

    /// <summary>Replaces the user-added extensions (".jfif2" → Image). Invalid entries are ignored.</summary>
    public static void SetExtra(IReadOnlyDictionary<string, FileKind>? extra)
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, kind) in extra ?? new Dictionary<string, FileKind>())
        {
            var e = NormalizeExtension(ext);
            if (e is not null && Assignable.Contains(kind)) map[e] = kind;
        }
        _extra = map;
    }

    /// <summary>".PNG", "png" and "*.png" all become ".png"; anything that is not a plain extension is null.</summary>
    public static string? NormalizeExtension(string? ext)
    {
        var e = (ext ?? "").Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        if (e.Length is 0 or > 16 || !e.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        return "." + e;
    }

    /// <summary>The kind DragDone knows for an extension without the user's additions.</summary>
    public static FileKind BuiltIn(string ext) => ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Other;

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        var ext = Path.GetExtension(path);
        if (_extra.TryGetValue(ext, out var extra)) return extra;
        return ByExtension.TryGetValue(ext, out var kind) ? kind : FileKind.Other;
    }

    public static FileKind Common(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return FileKind.Other;
        var first = Of(paths[0]);
        for (var i = 1; i < paths.Count; i++)
        {
            if (Of(paths[i]) != first) return FileKind.Mixed;
        }
        return first;
    }

    /// <summary>Plain text documents DragDone can lay out itself (no Office needed).</summary>
    public static bool IsPlainText(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".markdown" or ".csv" or ".tsv" or ".json" or ".xml" or ".log" or ".ini" or ".yaml"
            or ".yml" or ".html" or ".htm" or ".cs" or ".py" or ".js" or ".css" => true,
        _ => false,
    };

    public static bool IsOfficeDocument(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" or ".xls" or ".xlsx" or ".ods" or ".ppt" or ".pptx" or ".odp" => true,
        _ => false,
    };

    /// <summary>The Office application that opens a file, or null for anything Office does not own.</summary>
    public static OfficeApp? OfficeAppFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" => OfficeApp.Word,
        ".xls" or ".xlsx" or ".ods" or ".csv" => OfficeApp.Excel,
        ".ppt" or ".pptx" or ".odp" => OfficeApp.PowerPoint,
        _ => null,
    };

    /// <summary>Formats each Office application can save to, in ring order. PDF always comes first.</summary>
    public static IReadOnlyList<string> OfficeTargets(OfficeApp app) => app switch
    {
        OfficeApp.Word => WordTargets,
        OfficeApp.Excel => ExcelTargets,
        _ => PowerPointTargets,
    };

    static readonly string[] WordTargets = { "pdf", "docx", "doc", "odt", "rtf", "txt", "html" };
    static readonly string[] ExcelTargets = { "pdf", "xlsx", "xls", "ods", "csv" };
    static readonly string[] PowerPointTargets = { "pdf", "pptx", "ppt", "odp" };

    /// <summary>Normalised format token used to dim the "already this format" sector.</summary>
    public static string FormatToken(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpeg" or "jpe" or "jfif" => "jpg",
            "tif" => "tiff",
            "heif" => "heic",
            "m4v" => "mp4",
            "oga" => "ogg",
            "aif" => "aiff",
            "markdown" => "md",
            "htm" => "html",
            _ => ext,
        };
    }
}
