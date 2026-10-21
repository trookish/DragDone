using System.IO;
using System.Windows;
using DragDone.Core;
using DragDone.Native;
using DragDone.UI;
using Xunit;

namespace DragDone.Tests;

/// <summary>
/// Regressions found by driving the real app with a real Explorer drag. Each of these failed
/// in a live session and is cheap to reproduce here, so they are pinned down.
/// </summary>
public sealed class OverlayWindowTests
{
    sealed class Host : IOverlayHost
    {
        public Settings Settings { get; } = new();
        public Core.Capabilities Capabilities() => Core.Capabilities.All;
        public void RunInteractive(string actionId, DragPayload payload) { }
        public void RememberUndo(UndoRecord record) { }
    }

    static DragPayload Payload()
    {
        var file = Path.Combine(Path.GetTempPath(), "DragDoneOverlayTest.png");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        return new DragPayload(new[] { file });
    }

    /// <summary>WPF windows need an STA thread; xUnit runs tests on a worker thread.</summary>
    static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// A finished job animates Collapse to 1 with the default HoldEnd behaviour. Without clearing
    /// that animation the ring stayed invisible for the rest of the session: only the hub was drawn
    /// and every later drag looked broken.
    /// </summary>
    [Fact]
    public void Show_at_after_a_finished_job_restores_the_full_ring()
    {
        OnSta(() =>
        {
            var w = new OverlayWindow(new Host());
            try
            {
                w.ShowAt(new Win32.POINT { X = 100, Y = 100 }, MenuTab.Convert);

                // Simulate the collapse that RunJobAsync performs while a job runs.
                w.RunJobForTest("convert:jpg", Payload());

                // The next drag must bring the ring back.
                w.ShowAt(new Win32.POINT { X = 100, Y = 100 }, MenuTab.Convert);

                Assert.Equal(0.0, w.CollapseForTest);
            }
            finally
            {
                w.Close();
            }
        });
    }

    /// <summary>
    /// The window is sized from the ring, the key bar and the caption. It used to keep the
    /// constructor's placeholder size, which clipped the key bar and caption and pushed the
    /// ring off the cursor.
    /// </summary>
    [Fact]
    public void Window_is_sized_to_fit_the_ring_and_the_bar_below_it()
    {
        OnSta(() =>
        {
            var w = new OverlayWindow(new Host());
            try
            {
                w.ShowAt(new Win32.POINT { X = 100, Y = 100 }, MenuTab.Convert);
                w.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Assert.True(w.Width > 0 && w.Height > 0);
                // The ring plus the band underneath it must fit inside the window.
                Assert.True(w.Height > w.RingSizeForTest, "the window must be taller than the ring alone");
            }
            finally
            {
                w.Close();
            }
        });
    }
}
