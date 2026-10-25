using System.IO;
using System.Text;
using DragDone.Core;
using DragDone.Processors;
using Xunit;

namespace DragDone.Tests;

/// <summary>Regressions found in the 1.2.0 review: encodings, shared lists, batches and undo.</summary>
public sealed class AuditFixTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "dd-audit-" + Guid.NewGuid().ToString("N")[..8]);

    public AuditFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    const string Sample = "Hello, DragDone\r\nمرحبا بالعالم";

    [Fact]
    public void Text_saved_as_unicode_by_notepad_reads_correctly()
    {
        var le = Write("le.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Sample)).ToArray());
        var be = Write("be.txt", Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Sample)).ToArray());
        var u32 = Write("u32.txt", Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(Sample)).ToArray());
        Assert.Equal(Sample, DocumentOps.ReadText(le));
        Assert.Equal(Sample, DocumentOps.ReadText(be));
        Assert.Equal(Sample, DocumentOps.ReadText(u32));
    }

    [Fact]
    public void Utf16_without_a_byte_order_mark_is_recognised()
    {
        const string latin = "Quarterly report, final draft\r\nSecond line of plain text";
        Assert.Equal(latin, DocumentOps.ReadText(Write("le.txt", Encoding.Unicode.GetBytes(latin))));
        Assert.Equal(latin, DocumentOps.ReadText(Write("be.txt", Encoding.BigEndianUnicode.GetBytes(latin))));
    }

    [Fact]
    public void Utf8_and_windows_arabic_still_read_as_before()
    {
        Assert.Equal(Sample, DocumentOps.ReadText(Write("u8.txt", Encoding.UTF8.GetBytes(Sample))));
        Assert.Equal(Sample, DocumentOps.ReadText(Write("bom.txt", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Sample)).ToArray())));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(Sample, DocumentOps.ReadText(Write("1256.txt", Encoding.GetEncoding(1256).GetBytes(Sample))));
    }

    [Fact]
    public void Remembering_a_folder_never_changes_a_list_a_reader_holds()
    {
        var s = new Settings();
        s.RememberFolder(@"C:\a");
        s.RememberFolder(@"C:\b");
        var held = s.RecentFolders;
        var snapshot = held.ToList();

        s.RememberFolder(@"C:\c");
        s.RememberFolder(@"C:\A"); // same folder, different case: moves to the front

        Assert.Equal(snapshot, held);
        Assert.Equal(new[] { @"C:\A", @"C:\c", @"C:\b" }, s.RecentFolders);
        for (var i = 0; i < 10; i++) s.RememberFolder(@"C:\f" + i);
        Assert.Equal(5, s.RecentFolders.Count);
    }

    [Fact]
    public void A_locked_file_in_a_batch_keeps_the_other_outputs()
    {
        var good = OfficeSamples.Docx(_dir, "good.docx");
        var locked = OfficeSamples.Docx(_dir, "locked.docx");
        JobResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = ActionExecutor.Execute("doc.text", new DragPayload(new[] { good, locked }), new JobContext(new Settings()));

        Assert.Equal(JobOutcome.Created, result.Outcome);
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output));
        Assert.Contains("locked.docx", result.Detail);
        Assert.NotNull(result.Undo);
        Assert.Contains(output, result.Undo!.Created);
    }

    [Fact]
    public void A_hung_ffmpeg_is_given_up_on_after_the_time_limit()
    {
        // Prints a large banner to stderr and then hangs, as a broken shim or wrapper might.
        var fake = Path.Combine(_dir, "ffmpeg.cmd");
        File.WriteAllText(fake, "@echo off\r\nfor /l %%i in (1,1,400) do echo filler line to fill the stderr pipe %%i 1>&2\r\nping -n 30 127.0.0.1 >nul\r\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Ffmpeg.CanRun(fake));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 12);
    }

    [Fact]
    public void Undo_reports_a_move_it_could_not_put_back()
    {
        var from = Path.Combine(_dir, "original.txt");
        var to = Path.Combine(_dir, "moved.txt");
        File.WriteAllText(to, "moved");
        File.WriteAllText(from, "a new file now sits at the original path");

        var ok = ActionExecutor.Undo(new UndoRecord(Array.Empty<string>(), new[] { (from, to) }));

        Assert.False(ok);
        Assert.Equal("moved", File.ReadAllText(to));
        Assert.Equal("a new file now sits at the original path", File.ReadAllText(from));
    }

    [Fact]
    public void Undo_puts_a_move_back_and_reports_success()
    {
        var from = Path.Combine(_dir, "original.txt");
        var to = Path.Combine(_dir, "moved.txt");
        File.WriteAllText(to, "content");

        Assert.True(ActionExecutor.Undo(new UndoRecord(Array.Empty<string>(), new[] { (from, to) })));
        Assert.True(File.Exists(from));
        Assert.False(File.Exists(to));
    }
}
