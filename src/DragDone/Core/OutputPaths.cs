using DragDone.Localization;

namespace DragDone.Core;

/// <summary>Chooses where results go. Never overwrites: collisions get " (2)", " (3)", …</summary>
public static class OutputPaths
{
    /// <summary>Folder results are written to; null means "next to the source".</summary>
    public static string? OutputFolderOverride { get; set; }

    static string FolderFor(string source)
    {
        if (!string.IsNullOrWhiteSpace(OutputFolderOverride) && Directory.Exists(OutputFolderOverride))
            return OutputFolderOverride!;
        return Path.GetDirectoryName(Path.GetFullPath(source)) ?? Environment.CurrentDirectory;
    }

    /// <summary>
    /// <c>photo.heic</c> + <c>png</c> → <c>photo.png</c>;
    /// with a suffix key: <c>photo - Compressed.jpg</c> / <c>photo - مضغوط.jpg</c>.
    /// </summary>
    public static string For(string source, string extension, string? suffixKey = null, string? suffixLiteral = null)
    {
        var stem = Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/'));
        var suffix = suffixLiteral ?? (suffixKey is null ? null : Loc.T(suffixKey));
        var name = suffix is null ? stem : $"{stem} - {suffix}";
        return Unique(Path.Combine(FolderFor(source), Sanitize(name) + NormalizeExt(extension)));
    }

    /// <summary>A folder next to the source, e.g. <c>report - pages</c>.</summary>
    public static string FolderNear(string source, string? suffixKey)
    {
        var stem = Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/'));
        var name = suffixKey is null ? stem : $"{stem} - {Loc.T(suffixKey)}";
        return UniqueDirectory(Path.Combine(FolderFor(source), Sanitize(name)));
    }

    public static string InFolder(string folder, string fileName) => Unique(Path.Combine(folder, Sanitize(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName)));

    static string NormalizeExt(string ext) => ext.Length == 0 ? "" : (ext[0] == '.' ? ext : "." + ext);

    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public static string UniqueDirectory(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{path} ({i})";
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return $"{path} ({Guid.NewGuid():N})";
    }

    static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
