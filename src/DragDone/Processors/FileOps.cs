using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.Processors;

/// <summary>Zip, extract, hash, move, print.</summary>
public static class FileOps
{
    /// <summary>
    /// Asks the shell to print a file through the registered <c>print</c> verb, which is what a
    /// right-click → Print does. The handler owns the dialog and the spooler, so this only reports
    /// that the request was handed over; a file type with no print verb is what fails here.
    /// </summary>
    public static void Print(string path)
    {
        try
        {
            var info = new ProcessStartInfo(path)
            {
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new UserFacingException(Loc.T("error.print"));
        }
    }

    /// <summary>Removes a folder we created for outputs, without masking the error that caused it.</summary>
    public static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover empty folder is not worth hiding the real failure.
        }
    }

    // ------------------------------------------------------------------ zip

    public static string Zip(IReadOnlyList<string> paths, JobContext ctx)
    {
        var first = paths[0];
        var output = paths.Count == 1
            ? OutputPaths.For(first, "zip")
            : OutputPaths.For(first, "zip", null, Loc.F("suffix.andMore", ("n", paths.Count - 1)));

        var entries = new List<(string File, string Entry)>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var root = Path.GetFileName(p.TrimEnd('\\', '/'));
                var files = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
                if (files.Count == 0) entries.Add((p, root + "/"));
                foreach (var f in files) entries.Add((f, Path.Combine(root, Path.GetRelativePath(p, f)).Replace('\\', '/')));
            }
            else if (File.Exists(p))
            {
                entries.Add((p, Path.GetFileName(p)));
            }
        }
        var total = Math.Max(1, entries.Where(e => !e.Entry.EndsWith('/')).Sum(e => new FileInfo(e.File).Length));
        long done = 0;
        var tmp = output + ".dragdone-tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (file, entry) in entries)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    if (entry.EndsWith('/'))
                    {
                        zip.CreateEntry(entry);
                        continue;
                    }
                    // Already-compressed media gains nothing from deflate; store it fast.
                    var level = FileKinds.Of(file) is FileKind.Video or FileKind.Audio or FileKind.Archive || Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp"
                        ? CompressionLevel.Fastest
                        : CompressionLevel.Optimal;
                    zip.CreateEntryFromFile(file, entry, level);
                    done += new FileInfo(file).Length;
                    ctx.ReportOverall((double)done / total);
                }
            }
            File.Move(tmp, output);
        }
        finally
        {
            Ffmpeg.TryDelete(tmp);
        }
        return output;
    }

    // ------------------------------------------------------------------ extract

    public static string Extract(string archive, JobContext ctx)
    {
        var dest = OutputPaths.FolderNear(archive, null);
        Directory.CreateDirectory(dest);
        try
        {
            if (Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: false);
            }
            else
            {
                // Windows ships bsdtar (libarchive): tar, gz, bz2, xz, and on recent builds 7z and rar.
                var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
                if (!File.Exists(tar)) throw new UserFacingException(Loc.T("error.archive"));
                var psi = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-xf");
                psi.ArgumentList.Add(archive);
                psi.ArgumentList.Add("-C");
                psi.ArgumentList.Add(dest);
                using var p = Process.Start(psi)!;
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new UserFacingException(Loc.T("error.archive"));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            TryDeleteDir(dest);
            throw new UserFacingException(Loc.T("error.archive"));
        }
        catch (UserFacingException)
        {
            TryDeleteDir(dest);
            throw;
        }
        return dest;
    }

    static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ hash

    public static string Sha256(string path, JobContext ctx)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        long read = 0;
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ctx.Token.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, n, null, 0);
            read += n;
            ctx.Report(fs.Length == 0 ? 1 : (double)read / fs.Length);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ move

    public static IReadOnlyList<(string From, string To)> Move(IReadOnlyList<string> paths, string folder, JobContext ctx)
    {
        Directory.CreateDirectory(folder);
        var moves = new List<(string, string)>();
        for (var i = 0; i < paths.Count; i++)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var src = paths[i];
            var parent = Path.GetDirectoryName(Path.GetFullPath(src).TrimEnd('\\'));
            if (string.Equals(parent?.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                continue; // already there
            var target = OutputPaths.Unique(Path.Combine(folder, Path.GetFileName(src.TrimEnd('\\'))));
            MovePath(src, target);
            moves.Add((src, target));
            ctx.ReportOverall((i + 1.0) / paths.Count);
        }
        return moves;
    }

    public static void MovePath(string from, string to)
    {
        if (Directory.Exists(from))
        {
            if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(from, to);
            }
            else
            {
                CopyDirectory(from, to);
                Directory.Delete(from, true);
            }
        }
        else
        {
            File.Move(from, to);
        }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public static string? KnownFolder(string key) => key switch
    {
        "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "downloads" => DownloadsFolder(),
        _ => null,
    };

    static string DownloadsFolder()
    {
        if (Native.Win32.SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0)
            return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
