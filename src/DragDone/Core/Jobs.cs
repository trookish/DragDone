namespace DragDone.Core;

public enum JobOutcome
{
    /// <summary>New files or folders were written.</summary>
    Created,

    /// <summary>Nothing written; a message (and optional clipboard text) is the result.</summary>
    Info,

    /// <summary>The file was already as small or as trimmed as the action could make it.</summary>
    Unchanged,
    Cancelled,
    Failed,
}

public sealed record UndoRecord(IReadOnlyList<string> Created, IReadOnlyList<(string From, string To)> Moves)
{
    public static UndoRecord ForCreated(IEnumerable<string> paths) => new(paths.ToList(), Array.Empty<(string, string)>());
    public bool IsEmpty => Created.Count == 0 && Moves.Count == 0;
}

public sealed record JobResult
{
    public required JobOutcome Outcome { get; init; }
    public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();

    /// <summary>Catalogue key of the headline shown in the result chip.</summary>
    public required string MessageKey { get; init; }

    /// <summary>Extra text (an error, a hash, a count). Shown under the headline.</summary>
    public string? Detail { get; init; }

    /// <summary>Text to place on the clipboard when the job finishes (copy path, hashes).</summary>
    public string? Clipboard { get; init; }

    public UndoRecord? Undo { get; init; }

    public static JobResult Created(IReadOnlyList<string> outputs, string key = "result.done", string? detail = null) =>
        new() { Outcome = JobOutcome.Created, Outputs = outputs, MessageKey = key, Detail = detail, Undo = UndoRecord.ForCreated(outputs) };

    public static JobResult Fail(string detail) => new() { Outcome = JobOutcome.Failed, MessageKey = "result.failed", Detail = detail };
}

/// <summary>Per-job services handed to processors.</summary>
public sealed class JobContext
{
    readonly IProgress<double>? _progress;
    int _index;
    int _count = 1;

    public JobContext(Settings settings, IProgress<double>? progress = null, CancellationToken token = default)
    {
        Settings = settings;
        _progress = progress;
        Token = token;
    }

    public Settings Settings { get; }
    public CancellationToken Token { get; }

    /// <summary>Tell the context which item of a batch is running so progress spans the batch.</summary>
    public void BeginItem(int index, int count)
    {
        _index = index;
        _count = Math.Max(1, count);
        Report(0);
    }

    /// <summary>Progress of the current item, 0..1.</summary>
    public void Report(double fraction)
    {
        fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        _progress?.Report((_index + fraction) / _count);
    }

    /// <summary>Report progress of the whole job directly, 0..1.</summary>
    public void ReportOverall(double fraction) => _progress?.Report(Math.Clamp(fraction, 0, 1));
}

/// <summary>Raised by processors for a failure the user should read (already localised).</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>Raised when an action had nothing to do (file already optimal, nothing to trim).</summary>
public sealed class UnchangedException(string messageKey) : Exception(messageKey)
{
    public string MessageKey { get; } = messageKey;
}
