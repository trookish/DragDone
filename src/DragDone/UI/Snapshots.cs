using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>
/// Renders the overlay in representative states to PNG files, so the look can be checked
/// (and documented) without performing real drags.
/// </summary>
public static class Snapshots
{
    sealed class FakeHost : IOverlayHost
    {
        public FakeHost(Settings? settings = null) => Settings = settings ?? new Settings();
        public Settings Settings { get; }
        public Capabilities Capabilities() => Core.Capabilities.All;
        public void RunInteractive(string actionId, DragPayload payload) { }
        public void RememberUndo(UndoRecord record) { }
    }

    /// <summary>The look the snapshot matrix is currently rendering.</summary>
    static Settings _look = new();

    /// <summary>Creates a placeholder file with a plausible size so the hub shows real numbers.</summary>
    static string Sample(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "DragDoneSamples", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var size = FileKinds.Of(path) switch
        {
            FileKind.Video => 48_300_000,
            FileKind.Audio => 3_900_000,
            FileKind.Image => 2_400_000,
            FileKind.Pdf => 1_250_000,
            _ => 86_000,
        };
        if (!File.Exists(path) || new FileInfo(path).Length != size)
        {
            using var fs = File.Create(path);
            fs.SetLength(size);
        }
        return path;
    }

    public static int Render(string folder, string[] languages)
    {
        Directory.CreateDirectory(folder);
        var langs = languages.Length > 0 ? languages : new[] { "ar", "en" };
        var count = 0;

        // Every preset in every layout, in both languages: the matrix exists to prove the
        // customisation is real, not a tint of one wheel.
        foreach (var lang in langs)
        {
            Loc.Set(lang);
            foreach (var preset in Theme.PresetIds)
            {
                foreach (var layout in new[] { MenuLayout.Donut, MenuLayout.Pie, MenuLayout.Orbit })
                {
                    _look = new Settings { ThemePreset = preset, Layout = layout, Theme = ModeFor(preset) };
                    Theme.Attach(_look);
                    Save(folder, $"{lang}-{preset}-{LayoutName(layout)}-convert", Overlay(new[] { Sample("photo.heic") }, MenuTab.Convert, 2));
                    count++;
                }
                _look = new Settings { ThemePreset = preset, Layout = MenuLayout.Auto, Theme = ModeFor(preset) };
                Theme.Attach(_look);
                Save(folder, $"{lang}-{preset}-tools", Overlay(new[] { Sample("photo.jpg") }, MenuTab.Tools, 0));
                Save(folder, $"{lang}-{preset}-actions", Overlay(new[] { Sample("photo.jpg") }, MenuTab.Actions, 1));
                Save(folder, $"{lang}-{preset}-multi", Overlay(Enumerable.Range(1, 6).Select(i => Sample($"scan{i}.png")).ToArray(), MenuTab.Convert, 0));
                Save(folder, $"{lang}-{preset}-result", Result(Sample("report.png")));
                count += 4;
            }
        }

        // Density and label behaviour, on one representative menu.
        foreach (var lang in new[] { "en" })
        {
            Loc.Set(lang);
            foreach (var density in new[] { Density.Compact, Density.Roomy, Density.Large })
            {
                _look = new Settings { ThemePreset = "graphite", Density = density, Theme = ColorMode.Dark };
                Theme.Attach(_look);
                Save(folder, $"density-{density.ToString().ToLowerInvariant()}", Overlay(new[] { Sample("photo.jpg") }, MenuTab.Convert, 1));
                count++;
            }
            foreach (var labels in new[] { LabelStyle.Icons, LabelStyle.Labels, LabelStyle.Text })
            {
                _look = new Settings { ThemePreset = "paper", Labels = labels, Theme = ColorMode.Light };
                Theme.Attach(_look);
                Save(folder, $"labels-{labels.ToString().ToLowerInvariant()}", Overlay(new[] { Sample("photo.jpg") }, MenuTab.Convert, 1));
                count++;
            }
        }

        _look = new Settings();
        Theme.Attach(_look);
        Loc.Set("ar");
        Console.WriteLine($"rendered {count} snapshots to {folder}");
        return 0;
    }

    static FrameworkElement Overlay(string[] files, MenuTab tab, int hover, string? sub = null)
    {
        var w = new OverlayWindow(new FakeHost(_look));
        return w.SnapshotRoot(new DragPayload(files), tab, hover, sub);
    }

    static FrameworkElement Result(string output)
    {
        var w = new OverlayWindow(new FakeHost(_look));
        return w.SnapshotResult(new JobResult
        {
            Outcome = JobOutcome.Created,
            Outputs = new[] { output },
            MessageKey = "result.done",
            Undo = new UndoRecord(new[] { output }, Array.Empty<(string, string)>()),
        });
    }

    static void Save(string folder, string name, FrameworkElement element)
    {
        // A desktop-like backdrop so the ring reads the way it does over a real window.
        var backdrop = new Grid
        {
            Width = element.Width + 60,
            Height = element.Height + 60,
            Background = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x3F), Color.FromRgb(0x3A, 0x4E, 0x63), 45),
            FlowDirection = Loc.Flow,
        };
        element.Margin = new Thickness(30);
        backdrop.Children.Add(element);
        backdrop.Measure(new Size(backdrop.Width, backdrop.Height));
        backdrop.Arrange(new Rect(0, 0, backdrop.Width, backdrop.Height));
        backdrop.UpdateLayout();
        const double scale = 1.5;
        var bmp = new RenderTargetBitmap((int)(backdrop.Width * scale), (int)(backdrop.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(backdrop);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(folder, name + ".png"));
        enc.Save(fs);
        backdrop.Children.Clear();
    }

    /// <summary>Presets that are designed for one mode are shown in it; the rest follow Windows.</summary>
    static ColorMode ModeFor(string preset) => preset switch
    {
        "paper" => ColorMode.Light,
        "graphite" or "terminal" or "vivid" or "contrast" => ColorMode.Dark,
        _ => ColorMode.System,
    };

    static string LayoutName(MenuLayout layout) => layout.ToString().ToLowerInvariant();

    /// <summary>Opens one secondary window standalone; the process exits when it closes.</summary>
    /// <param name="capturePath">
    /// When set, the window is shown off-screen without activation, its client area is rendered
    /// to this PNG once the first frame is up, and the process exits. The user's screen is untouched.
    /// </param>
    public static void ShowWindow(string which, string? lang, string? capturePath = null, bool live = false, string? filter = null, double? scroll = null)
    {
        var offscreen = capturePath is not null;
        var settings = live ? Settings.Load() : new Settings();
        // No language given means "whatever the app would use", which is the system's language
        // unless the user pinned one.
        Loc.Set(lang ?? settings.ResolvedLanguage());
        // Opening a window saves on close, so point the harness at a scratch folder: capturing a
        // screenshot must never touch the settings or theme the user actually runs with.
        Settings.Folder = Path.Combine(Path.GetTempPath(), "DragDone-demo");
        Directory.CreateDirectory(Settings.Folder);
        Theme.Attach(settings);
        Window w = which switch
        {
            "settings" => new SettingsWindow(settings, () => Loc.Set(settings.ResolvedLanguage())),
            "crop" => new CropWindow(DemoImage(), "photo.jpg", 3),
            "rename" => new RenameWindow(Path.Combine(Path.GetTempPath(), "تقرير الربع الثالث.pdf")),
            "shelf" => DemoShelf(),
            _ => new WelcomeWindow(null),
        };
        if (filter is not null && w is SettingsWindow settingsWindow) settingsWindow.ApplyFilter(filter);
        w.Closed += (_, _) => Application.Current.Shutdown();
        if (offscreen)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.Topmost = false;
            w.ContentRendered += (_, _) => w.Dispatcher.BeginInvoke(() =>
            {
                // A scrolled capture has to wait for the page to measure and for the floating
                // preview to finish sliding in, or the shot catches it half way.
                if (scroll is { } offset && w is SettingsWindow shot)
                {
                    shot.ScrollToY(offset);
                    Pump(w.Dispatcher, 420);
                }
                CaptureClient(w, capturePath!);
                Application.Current.Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        w.Show();
    }

    /// <summary>Drains queued work for a while, so timers and animations reach their end state.</summary>
    static void Pump(System.Windows.Threading.Dispatcher dispatcher, int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// Exercises the settings window in-process and reports what it finds. No input is injected
    /// and nothing is shown on screen, so it is safe to run while another app is focused.
    /// </summary>
    public static int SelfTest(string? reportPath = null)
    {
        var problems = 0;
        var lines = new List<string>();
        var saveTo = Settings.Folder;
        Settings.Folder = Path.Combine(Path.GetTempPath(), "DragDone-selftest");
        Directory.CreateDirectory(Settings.Folder);
        try
        {
            var settings = new Settings();
            Loc.Set("en");
            Theme.Attach(settings);
            var window = new SettingsWindow(settings, () => { });
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            Pump(window.Dispatcher, 500);

            void Check(string what, bool passed, string detail)
            {
                lines.Add($"  {(passed ? "PASS" : "FAIL")}  {what,-38} {detail}");
                if (!passed) problems++;
            }

            var pills = Count<RadioButton>(window);
            var dropdowns = Count<ComboBox>(window);
            Check("options are multi-choice pills", pills >= 15, $"{pills} pills");
            Check("few options left in dropdowns", dropdowns <= 4, $"{dropdowns} dropdowns");

            Check("preview starts inline", !window.FloatingPreviewShown, "not floating at the top");

            window.ScrollToY(760);
            Pump(window.Dispatcher, 420);
            Check("preview floats once scrolled", window.FloatingPreviewShown,
                $"scroll {window.ScrollOffset:0}");

            var before = window.ScrollOffset;
            var wasDark = settings.Theme == ColorMode.Dark;
            settings.Theme = wasDark ? ColorMode.Light : ColorMode.Dark;
            Theme.Attach(settings);
            Pump(window.Dispatcher, 700);
            Check("a change keeps the scroll position", Math.Abs(window.ScrollOffset - before) < 2,
                $"{before:0} -> {window.ScrollOffset:0}");
            Check("preview still floating after the change", window.FloatingPreviewShown,
                $"scroll {window.ScrollOffset:0}");

            // The preview has to look right at the smallest and largest ring, not just at the
            // default: a crop that is too tight clips the outer labels, one that is too loose
            // leaves the ring small and off-centre with dead space around it.
            var wasDiameter = settings.MenuDiameter;
            foreach (var diameter in new[] { 240.0, 520.0 })
            {
                settings.MenuDiameter = diameter;
                window.RebuildNow();
                window.RedrawPreview();
                Pump(window.Dispatcher, 300);
                var shot = window.PreviewSource;
                if (shot is null)
                {
                    Check($"preview renders at {diameter:0}px", false, "no bitmap");
                    continue;
                }
                var ink = InkBounds(shot);
                var square = Math.Abs(shot.PixelWidth - shot.PixelHeight) <= 1;
                var inside = ink.Left > 6 && ink.Top > 6 &&
                             ink.Right < shot.PixelWidth - 6 && ink.Bottom < shot.PixelHeight - 6;
                Check($"preview is square and unclipped at {diameter:0}px", square && inside,
                    $"{shot.PixelWidth}x{shot.PixelHeight}, ink {ink.Left:0},{ink.Top:0} to {ink.Right:0},{ink.Bottom:0}");
                // The ring is placed by arithmetic, so check that, and check the ink sitting around
                // it is balanced enough that it reads as centred rather than pushed to one side.
                var offX = window.PreviewOffX;
                var offY = window.PreviewOffY;
                var slack = shot.PixelWidth * 0.06;
                var balanceX = Math.Abs(ink.Left - (shot.PixelWidth - ink.Right));
                var balanceY = Math.Abs(ink.Top - (shot.PixelHeight - ink.Bottom));
                Check($"preview ring is centred at {diameter:0}px",
                    offX <= 0.5 && offY <= 0.5 && balanceX <= slack + 12 && balanceY <= slack + 12,
                    $"placement {offX:0.0}x{offY:0.0}, ink balance {balanceX:0}x{balanceY:0}");
            }
            settings.MenuDiameter = wasDiameter;
            window.RebuildNow();
            window.RedrawPreview();
            Pump(window.Dispatcher, 300);

            // Changing a value is the common case: click a choice, drag a slider, tick a box. None of
            // those may move the page under the user.
            var choiceAt = window.ScrollOffset;
            var pill = FindAll<RadioButton>(window).FirstOrDefault(r => r.IsChecked != true && r.IsVisible);
            if (pill is not null)
            {
                pill.IsChecked = true;   // exactly what a click does
                Pump(window.Dispatcher, 600);
                Check("clicking a choice keeps the scroll", Math.Abs(window.ScrollOffset - choiceAt) < 4,
                    $"{choiceAt:0} -> {window.ScrollOffset:0}");
            }
            else
            {
                lines.Add("  SKIP  clicking a choice keeps the scroll      no unchecked pill");
            }

            var sliderAt = window.ScrollOffset;
            var slider = FindAll<Slider>(window).FirstOrDefault(s => s.IsVisible && s.IsEnabled);
            if (slider is not null)
            {
                var step = (slider.Maximum - slider.Minimum) * 0.1;
                slider.Value = Math.Min(slider.Maximum, slider.Value + step);
                Pump(window.Dispatcher, 600);
                Check("dragging a slider keeps the scroll", Math.Abs(window.ScrollOffset - sliderAt) < 4,
                    $"{sliderAt:0} -> {window.ScrollOffset:0} at {slider.Value:0}");
            }
            else
            {
                lines.Add("  SKIP  dragging a slider keeps the scroll      no visible slider");
            }

            window.ScrollToY(0);
            Pump(window.Dispatcher, 420);
            Check("floating copy hides at the top", !window.FloatingPreviewShown, "back inline");

            // Changing something that both rebuilds the page and repaints the window used to park
            // a half-restored position and then restore that, which is the jump users noticed.
            window.ScrollToY(760);
            Pump(window.Dispatcher, 420);
            var raceAt = window.ScrollOffset;
            window.RebuildNow();
            settings.Theme = settings.Theme == ColorMode.Dark ? ColorMode.Light : ColorMode.Dark;
            Theme.Attach(settings);
            Pump(window.Dispatcher, 900);
            Check("a rebuild and a theme change keep the scroll", Math.Abs(window.ScrollOffset - raceAt) < 4,
                $"{raceAt:0} -> {window.ScrollOffset:0}");
            settings.Theme = wasDark ? ColorMode.Dark : ColorMode.Light;
            Theme.Attach(settings);
            Pump(window.Dispatcher, 700);
            Check("and the page is intact afterwards", Count<RadioButton>(window) >= 15,
                $"{Count<RadioButton>(window)} pills");
            window.ScrollToY(0);
            Pump(window.Dispatcher, 300);

            // Dragging selects the corner that was dropped nearest; check the mapping itself.
            var area = new Rect(0, 0, 400, 600);
            var corners = new (Point At, PreviewCorner Want, string Name)[]
            {
                (new Point(80, 90), PreviewCorner.TopLeft, "top-left"),
                (new Point(320, 90), PreviewCorner.TopRight, "top-right"),
                (new Point(80, 520), PreviewCorner.BottomLeft, "bottom-left"),
                (new Point(320, 520), PreviewCorner.BottomRight, "bottom-right"),
            };
            foreach (var (at, want, name) in corners)
                Check($"a drop in the {name} picks it", SettingsWindow.DockAt(at, area) == want, want.ToString());

            // The remembered corner actually places the card there.
            window.ScrollToY(760);
            Pump(window.Dispatcher, 420);
            var defaultSpot = window.PreviewPosition;
            settings.PreviewDock = PreviewCorner.TopLeft;
            window.RefreshDock();
            Pump(window.Dispatcher, 320);
            var movedSpot = window.PreviewPosition;
            Check("the chosen corner moves the card",
                movedSpot.Y < defaultSpot.Y && movedSpot.X < defaultSpot.X,
                $"({defaultSpot.X:0},{defaultSpot.Y:0}) -> ({movedSpot.X:0},{movedSpot.Y:0})");
            settings.PreviewDock = PreviewCorner.BottomRight;
            window.RefreshDock();
            Pump(window.Dispatcher, 320);
            Check("and back again", window.PreviewPosition.Y > movedSpot.Y,
                $"({window.PreviewPosition.X:0},{window.PreviewPosition.Y:0})");

            settings.Theme = wasDark ? ColorMode.Dark : ColorMode.Light;
            window.Close();
            Pump(window.Dispatcher, 200);
        }
        catch (Exception ex)
        {
            lines.Add($"  FAIL  the settings window threw: {ex.GetType().Name}: {ex.Message}");
            problems++;
        }
        finally
        {
            Settings.Folder = saveTo;
        }

        lines.Add(problems == 0 ? "settings self-test: ok" : $"settings self-test: {problems} problem(s)");
        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);
        if (reportPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, report);
        }
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// Exercises the shelf in-process: that every file becomes a card, that the cards wrap into a
    /// grid rather than a single column, that each one carries its corner menu, and that removing
    /// an item rebuilds the grid.
    /// </summary>
    public static int SelfTestShelf(string? reportPath = null)
    {
        var problems = 0;
        var lines = new List<string>();
        try
        {
            Loc.Set("en");
            Theme.Attach(new Settings());
            var shelf = ShelfWindow.Instance;
            shelf.WindowStartupLocation = WindowStartupLocation.Manual;
            shelf.Left = -20000;
            shelf.Top = -20000;
            shelf.ShowActivated = false;
            shelf.ShowInTaskbar = false;
            shelf.Show();
            Pump(shelf.Dispatcher, 400);

            void Check(string what, bool passed, string detail)
            {
                lines.Add($"  {(passed ? "PASS" : "FAIL")}  {what,-38} {detail}");
                if (!passed) problems++;
            }

            var dir = Path.Combine(Path.GetTempPath(), "DragDone-shelf-test");
            Directory.CreateDirectory(dir);
            var files = new[]
            {
                SampleImage(dir, "one.png", Color.FromRgb(0x30, 0x40, 0x60), Color.FromRgb(0x10, 0x18, 0x28)),
                Path.Combine(dir, "two.pdf"),
                Path.Combine(dir, "three.mp4"),
                Path.Combine(dir, "four.docx"),
                Path.Combine(dir, "five.zip"),
                Path.Combine(dir, "six.txt"),
            };
            foreach (var f in files.Where(f => !File.Exists(f))) File.WriteAllText(f, "sample");
            foreach (var f in files) shelf.Add(new[] { f });
            Pump(shelf.Dispatcher, 500);

            var cards = shelf.CardHost.Children.OfType<FrameworkElement>().ToList();
            Check("every file becomes a card", cards.Count == files.Length && shelf.ItemCount == files.Length,
                $"{cards.Count} cards for {files.Length} files");

            // A grid means the second card sits on the same row as the first, not below it.
            var first = cards[0].TranslatePoint(new Point(0, 0), shelf.CardHost);
            var second = cards[1].TranslatePoint(new Point(0, 0), shelf.CardHost);
            var third = cards[2].TranslatePoint(new Point(0, 0), shelf.CardHost);
            Check("cards sit side by side", Math.Abs(second.Y - first.Y) < 1 && second.X > first.X,
                $"({first.X:0},{first.Y:0}) and ({second.X:0},{second.Y:0})");
            Check("and wrap onto a second row", third.Y > first.Y + 1, $"row 2 at y={third.Y:0}");

            // A context menu is a popup, not a visual child, so read it from the card's button.
            var menus = cards.Select(card => Find<Button>(card)?.ContextMenu).ToList();
            Check("every card carries its corner menu", menus.All(m => m is not null),
                $"{menus.Count(m => m is not null)}/{cards.Count}");
            Check("the menu offers open, reveal and remove",
                menus.All(m => m is not null && m.Items.OfType<MenuItem>().Count() >= 3),
                $"{menus.FirstOrDefault()?.Items.OfType<MenuItem>().Count() ?? 0} entries");

            var before = cards.Count;
            shelf.RemoveFirst();
            Pump(shelf.Dispatcher, 400);
            Check("removing one rebuilds the grid", shelf.CardHost.Children.Count == before - 1,
                $"{before} -> {shelf.CardHost.Children.Count}");

            shelf.Clear();
            Pump(shelf.Dispatcher, 300);
            Check("clearing empties it", shelf.ItemCount == 0, $"{shelf.ItemCount} left");
            shelf.Hide();
        }
        catch (Exception ex)
        {
            lines.Add($"  FAIL  the shelf threw: {ex.GetType().Name}: {ex.Message}");
            problems++;
        }

        lines.Add(problems == 0 ? "shelf self-test: ok" : $"shelf self-test: {problems} problem(s)");
        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);
        if (reportPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, report);
        }
        return problems == 0 ? 0 : 1;
    }

    /// <summary>Bounding box of everything that is not the plate's flat background.</summary>
    static (double Left, double Top, double Right, double Bottom) InkBounds(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        // The plate is painted with one flat colour, so the first pixel is a safe background key.
        byte br = pixels[0], bg = pixels[1], bb = pixels[2];
        int minX = bitmap.PixelWidth, minY = bitmap.PixelHeight, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (Math.Abs(pixels[i] - br) < 10 && Math.Abs(pixels[i + 1] - bg) < 10 && Math.Abs(pixels[i + 2] - bb) < 10)
                    continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0
            ? (0, 0, bitmap.PixelWidth, bitmap.PixelHeight)
            : (minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Every descendant of a type, for driving real controls from a test.</summary>
    static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        void Walk(DependencyObject node)
        {
            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T hit) found.Add(hit);
                Walk(child);
            }
        }
        Walk(root);
        return found;
    }

    /// <summary>The first descendant of a type, for inspecting a built card.</summary>
    static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Counts descendants of a type in a live visual tree.</summary>
    static int Count<T>(DependencyObject root) where T : DependencyObject
    {
        var found = 0;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) found++;
            found += Count<T>(child);
        }
        return found;
    }

    /// <summary>Renders the window's client area (its content over its own background) at device DPI.</summary>
    static void CaptureClient(Window w, string path)
    {
        // The window template's root spans the whole client area, content margins included.
        if (VisualTreeHelper.GetChildrenCount(w) == 0 || VisualTreeHelper.GetChild(w, 0) is not FrameworkElement content) return;
        var dpi = VisualTreeHelper.GetDpi(w);
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            // Fluent windows are transparent over Mica; composite over its dark base colour
            // or the translucent card brushes lose their meaning.
            var background = w.Background is SolidColorBrush { Color.A: 255 } solid ? solid : new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            dc.DrawRectangle(background, null, bounds);
            // The template root carries WPF's RTL mirror transform, so this matches the screen as-is.
            dc.DrawRectangle(new VisualBrush(content)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            }, null, bounds);
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    static Window DemoShelf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DragDoneSamples");
        Directory.CreateDirectory(dir);
        // Real pictures for the picture cards, so the capture shows what a shelf of photos looks
        // like rather than a wall of the same generic icon.
        var files = new List<string>
        {
            SampleImage(dir, "sunset.png", Color.FromRgb(0xF2, 0xA6, 0x5A), Color.FromRgb(0x2B, 0x5D, 0x9C)),
            SampleImage(dir, "harbour.png", Color.FromRgb(0x2E, 0x8B, 0x9A), Color.FromRgb(0x10, 0x2A, 0x3F)),
            SampleImage(dir, "الغروب.png", Color.FromRgb(0xC9, 0x5B, 0x6E), Color.FromRgb(0x3A, 0x1F, 0x4D)),
            Path.Combine(dir, "تقرير الربع الثالث.pdf"),
            Path.Combine(dir, "clip.mp4"),
            Path.Combine(dir, "notes.docx"),
        };
        foreach (var f in files.Where(f => !File.Exists(f))) File.WriteAllText(f, "sample");
        ShelfWindow.Instance.Add(files);
        ShelfWindow.Instance.ShowInTaskbar = true;
        return ShelfWindow.Instance;
    }

    /// <summary>A real PNG so a demo card has a picture in it.</summary>
    static string SampleImage(string dir, string name, Color from, Color to)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new LinearGradientBrush(from, to, 60), null, new Rect(0, 0, 240, 160));
        var bmp = new RenderTargetBitmap(240, 160, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    static BitmapSource DemoImage()
    {
        // A gradient "photo" with a horizon, so crop handles have something to sit on.
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x5D, 0x9C), Color.FromRgb(0xF2, 0xA6, 0x5A), 90), null, new Rect(0, 0, 1600, 1000));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1D, 0x3B, 0x2A)), null, new Rect(0, 680, 1600, 320));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x9A)), null, new Point(1100, 560), 110, 110);
        }
        var bmp = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }
}
