using System.Diagnostics;

namespace DragDone.Core;

/// <summary>
/// Finds out which Office applications this machine can automate.
///
/// Availability is a registry question (ProgID → CLSID), not a file-kind question: a .docx is a
/// Word document on a machine with no Word, it simply cannot be converted, and the menu should say
/// so instead of hiding the option. The answer is cached for the life of the process because every
/// ring build would otherwise read the registry five times.
/// </summary>
public static class OfficeApps
{
    static readonly object Gate = new();
    static readonly OfficeFamily[] Families =
    {
        OfficeFamily.Word, OfficeFamily.Excel, OfficeFamily.PowerPoint, OfficeFamily.Visio, OfficeFamily.Publisher,
    };

    static HashSet<OfficeFamily>? _installed;

    /// <summary>The ProgID Office registers for the app that owns <paramref name="family"/>.</summary>
    public static string? ProgId(OfficeFamily family) => family switch
    {
        OfficeFamily.Word => "Word.Application",
        OfficeFamily.Excel => "Excel.Application",
        OfficeFamily.PowerPoint => "PowerPoint.Application",
        OfficeFamily.Visio => "Visio.Application",
        OfficeFamily.Publisher => "Publisher.Application",
        _ => null,
    };

    /// <summary>The executable Office runs as, used to tell "already open" from "we started it".</summary>
    public static string? ProcessName(OfficeFamily family) => family switch
    {
        OfficeFamily.Word => "WINWORD",
        OfficeFamily.Excel => "EXCEL",
        OfficeFamily.PowerPoint => "POWERPNT",
        OfficeFamily.Visio => "VISIO",
        OfficeFamily.Publisher => "MSPUB",
        _ => null,
    };

    /// <summary>True when the app is registered well enough for <c>Activator.CreateInstance</c>.</summary>
    public static bool Installed(OfficeFamily family)
    {
        if (family == OfficeFamily.None) return false;
        var set = _installed;
        if (set is null)
        {
            lock (Gate)
            {
                set = _installed ??= Detect();
            }
        }
        return set.Contains(family);
    }

    /// <summary>Whether any Office document can be converted on this machine.</summary>
    public static bool Any => Families.Any(Installed);

    /// <summary>True when a process for <paramref name="family"/> is already running.</summary>
    public static bool IsRunning(OfficeFamily family)
    {
        if (ProcessName(family) is not { } name) return false;
        try
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Cannot tell; assume the user's session is there so we never quit their app.
            return true;
        }
    }

    /// <summary>
    /// The capabilities of this machine: ffmpeg, every Office app found, and the folders the user
    /// moved files to recently.
    /// </summary>
    public static Capabilities Detect(bool ffmpeg, IReadOnlyList<string> recentFolders) =>
        new(ffmpeg, Any, recentFolders,
            Word: Installed(OfficeFamily.Word),
            Excel: Installed(OfficeFamily.Excel),
            PowerPoint: Installed(OfficeFamily.PowerPoint),
            Visio: Installed(OfficeFamily.Visio),
            Publisher: Installed(OfficeFamily.Publisher));

    static HashSet<OfficeFamily> Detect()
    {
        var found = new HashSet<OfficeFamily>();
        foreach (var family in Families)
        {
            if (Registered(family)) found.Add(family);
        }
        return found;
    }

    static bool Registered(OfficeFamily family)
    {
        if (ProgId(family) is not { } progId) return false;
        try
        {
            // Type.GetTypeFromProgID reads HKCR; a half-uninstalled Office throws instead of
            // returning null, and that must mean "not usable", not "crash the menu".
            return Type.GetTypeFromProgID(progId) is not null;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
