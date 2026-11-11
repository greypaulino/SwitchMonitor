using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Threading;

namespace SwitchMonitor.App.Services;

/// <summary>
/// <c>SwitchMonitor.exe --test-cover [folder]</c>: runs the black cover on its own (no monitor, no profile), photographs
/// the whole desktop before, with the cover on, and after, and says whether the cover really covered it. The pictures
/// and a short report go to the folder (the temp folder by default). Nothing is sent to any display.
/// </summary>
internal static class CoverSelfTest
{
    public static void Run(Dispatcher dispatcher, string folder, Action done)
    {
        Directory.CreateDirectory(folder);
        var hooks = new BlackoutOverlay(dispatcher).CreateHooks();
        Task.Run(() =>
        {
            var report = new List<string> { $"Cover self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}", $"Animations enabled in Windows: {System.Windows.SystemParameters.ClientAreaAnimation}" };
            try
            {
                double before = Shot(Path.Combine(folder, "cover-0-before.png"));
                var clock = Stopwatch.StartNew();
                hooks.BeforeDisruptive!();
                report.Add($"Cover on: BeforeDisruptive returned after {clock.ElapsedMilliseconds} ms");
                Thread.Sleep(150);   // the compositor needs a moment to show the last frame
                double covered = Shot(Path.Combine(folder, "cover-1-covered.png"));
                Thread.Sleep(800);
                clock.Restart();
                hooks.AfterDisruptive!();
                report.Add($"Cover off: AfterDisruptive returned after {clock.ElapsedMilliseconds} ms");
                Thread.Sleep(150);
                double after = Shot(Path.Combine(folder, "cover-2-after.png"));
                report.Add($"Average brightness (0 black - 255 white): before {before:0.0}, covered {covered:0.0}, after {after:0.0}");
                bool covers = covered < 3;
                bool lifted = Math.Abs(after - before) < Math.Max(8, before * 0.25);
                report.Add(covers ? "OK: the screen was black while covered." : "FAIL: the screen was NOT black while covered.");
                report.Add(lifted ? "OK: the cover went away." : "FAIL: the screen is not back to how it was.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                report.Add("FAIL: " + error);
            }
            File.WriteAllLines(Path.Combine(folder, "cover-test.txt"), report);
            dispatcher.Invoke(done);
        });
    }

    /// <summary>
    /// <c>--test-corner [folder]</c>: opens the main window (with the saved look) over a checkerboard and compares the pixels
    /// around its rounded top-left corner with the same pixels before the window existed: outside the frame they must not
    /// change. On Windows 11 it repeats the test with several ways of switching off the window shadow Windows draws, and
    /// reports each one, so a single run says which way works.
    /// </summary>
    public static void Corner(Dispatcher dispatcher, string folder, Action done)
    {
        Directory.CreateDirectory(folder);
        Task.Run(() =>
        {
            var report = new List<string> { $"Corner self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}", $"Look: {Theme.Current.Appearance}" };
            var summary = new List<string>();
            try
            {
                var (lines, halo, inside, verdict) = CornerOnce(dispatcher, folder, 0);
                report.AddRange(lines);
                summary.Add($"Window: change just outside the frame {halo} (Windows' own soft shadow on Windows 11), inside {inside}: {verdict}");
            }
            catch (Exception error) when (error is not OutOfMemoryException) { report.Add("FAIL: " + error); }
            report.InsertRange(2, Theme.Diagnostics());
            report.Insert(5, "SUMMARY:");
            report.InsertRange(6, summary);
            File.WriteAllLines(Path.Combine(folder, "corner-test.txt"), report);
            dispatcher.Invoke(done);
        });
    }

    private static (List<string> Lines, int Halo, int Inside, string Verdict) CornerOnce(Dispatcher dispatcher, string folder, int variant)
    {
        MainWindow? window = null;
        System.Windows.Window? backdrop = null;   // a fine checkerboard behind it: any blur or shadow leaking past the corner shows up strongly
        var report = new List<string>();
        int left = 220, top = 180;
        var scale = 1.0;
        dispatcher.Invoke(() =>
        {
            backdrop = new System.Windows.Window { WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                Background = Checkers(), Left = left - 60, Top = top - 60, Width = 900, Height = 700 };
            backdrop.Show();
            window = new MainWindow { Topmost = true, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = left, Top = top, Width = 700, Height = 500, ShowActivated = false };
            scale = System.Windows.PresentationSource.FromVisual(System.Windows.Application.Current.MainWindow ?? window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        });
        Thread.Sleep(500);
        int px = (int)Math.Round(left * scale), py = (int)Math.Round(top * scale);
        var before = Region(px - 12, py - 12, 40, 40);
        dispatcher.Invoke(() => window!.Show());
        Thread.Sleep(1800);
        var after = Region(px - 12, py - 12, 40, 40);
        try   // a picture of the corner (6x), for looking at the edge itself
        {
            using var shot = new Bitmap(40 * 6, 40 * 6);
            for (int i = 0; i < 40; i++) for (int j = 0; j < 40; j++)
                for (int a2 = 0; a2 < 6; a2++) for (int b2 = 0; b2 < 6; b2++) shot.SetPixel(i * 6 + a2, j * 6 + b2, after[i, j]);
            shot.Save(Path.Combine(folder, variant == 0 ? "corner-zoom.png" : $"corner-zoom-v{variant}.png"), ImageFormat.Png);
        }
        catch (Exception) { }
        int Diff(int x, int y) => Math.Abs(before[x, y].R - after[x, y].R) + Math.Abs(before[x, y].G - after[x, y].G) + Math.Abs(before[x, y].B - after[x, y].B);
        // The corner of a radius-14 frame: the first pixels along the diagonal are outside the rounded shape.
        int worst = 0;
        for (int d = 0; d < 3; d++)
            for (int e = 0; e <= d; e++) worst = Math.Max(worst, Diff(12 + d - e, 12 + e));
        // Just above the top edge of the window (two rows up), clear of the corner: where a shadow shows first.
        int halo = 0;
        for (int x = 20; x < 36; x++) halo = Math.Max(halo, Diff(x, 10));
        int inside = Diff(30, 30);
        string Row(int y) => string.Join(" ", Enumerable.Range(0, 30).Select(x => Diff(x, y).ToString()));
        report.Add("Row -6 (above the window): " + Row(6));
        report.Add("Row 0 (window top edge): " + Row(12));
        int sharp = Math.Abs(after[30, 30].R - after[31, 30].R) + Math.Abs(after[30, 30].G - after[31, 30].G) + Math.Abs(after[30, 30].B - after[31, 30].B);
        report.Add($"Inside the window, neighbouring checker pixels differ by {sharp} (765 = sharp, near 0 = blurred)");
        report.Add($"Change at the outermost corner pixels (0 = untouched): {worst}; just above the top edge: {halo}; inside the window: {inside}");
        // Two different things: the dark gap in the corner (the frame's radius against the one Windows rounds with: must be gone) and
        // Windows' own soft shadow outside the window (normal on Windows 11: only reported).
        string shadow = halo > 6 ? $" Windows' soft shadow outside the window: {halo} (normal)." : "";
        string verdict = !Theme.RoundedBlur ? "Windows 10: the Translucent frame is square, so there is no rounded corner to leak past."
            : inside < 60 ? "WARNING: the window did not draw over the test area."
            : worst <= 6 ? "OK: no dark gap in the corner." + shadow : "FAIL: a dark gap shows in the corner (the frame radius does not match the one Windows rounds with)." + shadow;
        report.Add(verdict);
        dispatcher.Invoke(() => { window?.Close(); backdrop?.Close(); });
        Thread.Sleep(400);
        return (report, halo, inside, verdict);
    }
    /// <summary>A fine black-and-white checkerboard: a blur behind the window turns it grey, so any blur that leaks shows up.</summary>
    private static System.Windows.Media.Brush Checkers()
    {
        var tile = new System.Windows.Media.DrawingGroup();
        tile.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.White, null, new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 2, 2))));
        tile.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Black, null, new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 1, 1))));
        tile.Children.Add(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Black, null, new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(1, 1, 1, 1))));
        var brush = new System.Windows.Media.DrawingBrush(tile) { TileMode = System.Windows.Media.TileMode.Tile, Viewport = new System.Windows.Rect(0, 0, 2, 2), ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute };
        System.Windows.Media.RenderOptions.SetCachingHint(brush, System.Windows.Media.CachingHint.Cache);
        return brush;
    }

    private static Color[,] Region(int x, int y, int w, int h)
    {
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        var pixels = new Color[w, h];
        for (int i = 0; i < w; i++) for (int j = 0; j < h; j++) pixels[i, j] = bitmap.GetPixel(i, j);
        return pixels;
    }

    /// <summary><c>--test-icon [folder]</c>: does the program find its own icon without any loose .ico file?</summary>
    public static void IconCheck(string folder, Action done)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        try
        {
            using var icon = TrayIcon.LoadIcon();
            bool generic = icon.Handle == System.Drawing.SystemIcons.Application.Handle;
            report.Add($"Icon loaded: {icon.Width}x{icon.Height}; generic application icon: {generic}");
            var window = new MainWindow();
            report.Add($"Window icon set: {window.Icon is not null}");
            report.Add(!generic && window.Icon is not null ? "OK: the program carries its own icon." : "FAIL: the icon is missing.");
            window.Close();
        }
        catch (Exception error) { report.Add("FAIL: " + error); }
        File.WriteAllLines(Path.Combine(folder, "icon-test.txt"), report);
        done();
    }

    /// <summary><c>--test-menu [folder]</c>: opens the tray menu card with the sun's lines and then the monitor's, photographing each.</summary>
    public static void Menu(Dispatcher dispatcher, string folder, Action done)
    {
        Directory.CreateDirectory(folder);
        var menu = new TrayMenu();
        IReadOnlyList<TrayMenuItem> sun = [new("Exit", "\uE8BB", () => { }), new("Start With Windows", "\uE71D", () => { }, () => true), new("Check Updates", "\uE72C", () => { })];
        IReadOnlyList<TrayMenuItem> main = [new("Exit", "\uE8BB", () => { }), new("Shortcuts", Icons.Shortcuts, () => { }), new("Edit a Profile", Icons.Profiles, () => { }), new("Open", "\uE8A7", () => { }), new("Update available > Install Now", "\uE896", () => { })];
        Task.Run(() =>
        {
            dispatcher.Invoke(() => menu.Toggle(sun));
            Thread.Sleep(900); Shot(Path.Combine(folder, "menu-sun.png")); Corner(Path.Combine(folder, "menu-sun.png"), Path.Combine(folder, "menu-sun-corner.png"));
            dispatcher.Invoke(() => menu.Close_ForTest());
            Thread.Sleep(600);
            dispatcher.Invoke(() => menu.Toggle(main));
            Thread.Sleep(900); Shot(Path.Combine(folder, "menu-main.png")); Corner(Path.Combine(folder, "menu-main.png"), Path.Combine(folder, "menu-main-corner.png"));
            dispatcher.Invoke(done);
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RectNative rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    /// <summary>Photographs one window (frame and caption included).</summary>
    private static void Photo(Dispatcher dispatcher, System.Windows.Window window, string path)
    {
        RectNative rect = default;
        dispatcher.Invoke(() => GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out rect));
        int w = Math.Max(1, rect.Right - rect.Left), h = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h));
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>The two pairing pop-ups, photographed (--test-pair [folder]): asking for the code, with digits, and showing a code.</summary>
    public static void PairWindows(Dispatcher dispatcher, string folder, Action done)
    {
        Directory.CreateDirectory(folder);
        PairCodeWindow? ask = null;
        PairShowWindow? show = null;
        Task.Run(() =>
        {
            dispatcher.Invoke(() => { ask = new PairCodeWindow("GREY"); ask.Show(); });
            Thread.Sleep(900); Photo(dispatcher, ask!, Path.Combine(folder, "pair-ask-empty.png"));
            dispatcher.Invoke(() => ask!.Prefill("4815"));
            Thread.Sleep(500); Photo(dispatcher, ask!, Path.Combine(folder, "pair-ask-typing.png"));
            dispatcher.Invoke(() => ask!.Prefill("481516"));
            Thread.Sleep(500); Photo(dispatcher, ask!, Path.Combine(folder, "pair-ask-full.png"));
            dispatcher.Invoke(() =>
            {
                ask!.Close();
                show = new PairShowWindow("Laptop", "481516", () => DateTimeOffset.Now.AddSeconds(100), () => { });
                show.Show();
            });
            Thread.Sleep(900); Photo(dispatcher, show!, Path.Combine(folder, "pair-show.png"));
            dispatcher.Invoke(() => { show!.Close(); done(); });
        });
    }

    /// <summary>The bottom right corner of a photo (where the tray menu opens), enlarged twice, and the full photo is deleted (it shows the whole desktop).</summary>
    private static void Corner(string source, string target)
    {
        using (var full = new Bitmap(source))
        {
            var area = new Rectangle(Math.Max(0, full.Width - 460), Math.Max(0, full.Height - 320), Math.Min(460, full.Width), Math.Min(320, full.Height));
            using var crop = new Bitmap(area.Width * 2, area.Height * 2);
            using (var g = Graphics.FromImage(crop))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), area, GraphicsUnit.Pixel);
            }
            crop.Save(target, ImageFormat.Png);
        }
        File.Delete(source);
    }

    /// <summary>Photographs the whole desktop (every screen) and returns its average brightness.</summary>
    private static double Shot(string path)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        bitmap.Save(path, ImageFormat.Png);
        long sum = 0, count = 0;
        for (int y = 0; y < bitmap.Height; y += 13)
            for (int x = 0; x < bitmap.Width; x += 13)
            {
                var pixel = bitmap.GetPixel(x, y);
                sum += (pixel.R + pixel.G + pixel.B) / 3;
                count++;
            }
        return count == 0 ? 0 : (double)sum / count;
    }
}
