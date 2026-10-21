using System.Text;

namespace DragDone.Core;

/// <summary>
/// Opt-in event trace for bug reports. Set the DRAGDONE_TRACE environment variable to a file path
/// and the drag pipeline appends one line per decision (press, eligibility, phase change, summon,
/// OLE enter/drop). With the variable unset every call returns immediately and nothing is written.
/// </summary>
public static class Diag
{
    static readonly string? Path = Environment.GetEnvironmentVariable("DRAGDONE_TRACE") is { Length: > 0 } p ? p : null;
    static readonly object Gate = new();

    public static bool Enabled => Path is not null;

    public static void Log(string message)
    {
        if (Path is null) return;
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,2}] {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(Path, line, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tracing must never be able to break the app.
        }
    }
}
