namespace DragDone.Core;

/// <summary>The files carried by one drag, classified once.</summary>
public sealed class DragPayload
{
    public IReadOnlyList<string> Paths { get; }
    public FileKind Kind { get; }
    public long TotalBytes { get; }

    /// <summary>Format token shared by every file, or null when they differ.</summary>
    public string? CommonFormat { get; }

    public int Count => Paths.Count;
    public bool IsMultiple => Paths.Count > 1;

    /// <summary>
    /// The Office application every file belongs to, or None when the drag is plain text, mixes
    /// families, or holds no Office documents at all.
    /// </summary>
    public OfficeFamily Office { get; }

    public DragPayload(IEnumerable<string> paths)
    {
        Paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Kind = FileKinds.Common(Paths);
        TotalBytes = Paths.Sum(SizeOf);
        Office = FileKinds.OfficeCommon(Paths);
        var formats = Paths.Select(FileKinds.FormatToken).Distinct().ToList();
        CommonFormat = formats.Count == 1 && Kind != FileKind.Folder ? formats[0] : null;
    }

    static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
            {
                // Bounded walk so a huge folder cannot stall the menu.
                long total = 0;
                var n = 0;
                foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    total += new FileInfo(f).Length;
                    if (++n > 2000) break;
                }
                return total;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    public bool AllFiles => Paths.All(File.Exists);
}
