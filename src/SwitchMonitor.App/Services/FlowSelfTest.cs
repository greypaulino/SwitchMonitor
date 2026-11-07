using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.Services;

/// <summary>
/// <c>SwitchMonitor.exe --test-flow [folder]</c>: the real profile pipeline (MainViewModel, hooks, cover, sliders) on the
/// in-memory demo monitor, no real display and no real settings. It records (1) how the brightness slider moves during a
/// gradual profile change and (2) how bright the screen is while a FreeSync change is covered, and writes a report and a CSV.
/// </summary>
internal static class FlowSelfTest
{
    public static void Run(Dispatcher dispatcher, string folder, Action done) =>
        dispatcher.InvokeAsync(async () =>
        {
            Directory.CreateDirectory(folder);
            var report = new List<string> { $"Flow self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            try
            {
                var log = NullLog.Instance;
                var model = new MainViewModel(log, new AppSettings())
                {
                    DeviceSource = () => DemoMonitors.Create("demo", log),
                    CoverInDemo = true
                };
                model.Transition = new BlackoutOverlay(dispatcher).CreateHooks();
                await model.RefreshAsync();
                var lg = model.Monitors.FirstOrDefault(m => m.IsControllable);
                if (lg is not null) await lg.EnsureLoadedAsync();
                if (lg?.Brightness is not { } slider) { report.Add("FAIL: the demo display has no brightness slider."); Finish(folder, report, done); return; }
                report.Add($"Display: {lg.Name}; brightness starts at {slider.Value:0}%");

                // ---- 1. The slider follows a gradual change (no FreeSync, no cover) ----
                var samples = new List<(long Ms, double Shown)>();
                var clock = Stopwatch.StartNew();
                var sampler = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => samples.Add((clock.ElapsedMilliseconds, slider.Shown)), dispatcher);
                sampler.Start();
                var move = SceneStore.Parse("""{ "scenes": [ { "name": "Move", "settings": { "brightness": 80 } } ] }""")[0];
                await model.ApplySceneAsync(move);
                await Task.Delay(500);
                sampler.Stop();
                var shown = samples.Select(s => s.Shown).ToArray();
                int distinct = shown.Select(v => Math.Round(v)).Distinct().Count();
                bool rising = shown.Zip(shown.Skip(1), (a, b) => b >= a - 0.01).All(x => x);
                long start = samples.FirstOrDefault(s => s.Shown > shown[0] + 1).Ms, end = samples.LastOrDefault(s => s.Shown < shown[^1] - 1).Ms;
                report.Add($"Slider: {shown[0]:0} -> {shown[^1]:0}, {distinct} different positions, moving for about {Math.Max(0, end - start)} ms, rising only: {rising}");
                report.Add(distinct >= 15 && rising && shown[^1] >= 79 ? "OK: the slider follows the gradual change."
                    : "FAIL: the slider does not follow the change smoothly.");
                File.WriteAllLines(Path.Combine(folder, "flow-slider.csv"), samples.Select(s => $"{s.Ms};{s.Shown:0.0}").Prepend("ms;slider"));

                // ---- 2. FreeSync under the cover: the way back must be a fade, not a cut ----
                var shots = new List<(long Ms, double Light)>();
                var stop = false;
                var shooter = Task.Run(() =>
                {
                    var c = Stopwatch.StartNew();
                    while (!stop) { shots.Add((c.ElapsedMilliseconds, Light())); Thread.Sleep(25); }
                });
                await Task.Delay(300);   // a baseline before the change
                var cover = SceneStore.Parse("""{ "scenes": [ { "name": "Cover", "settings": { "FreeSync": "On", "brightness": 30 } } ] }""")[0];
                var applied = Stopwatch.StartNew();
                await model.ApplySceneAsync(cover);
                report.Add($"Cover profile finished after {applied.ElapsedMilliseconds} ms");
                await Task.Delay(1200);   // time for the way back to finish
                stop = true; await shooter;
                double baseline = shots.Take(8).Average(s => s.Light);
                var darkAt = shots.Where(s => s.Light < baseline * 0.1).Select(s => s.Ms).DefaultIfEmpty(-1).ToArray();
                long firstDark = darkAt[0], lastDark = darkAt[^1];
                long downFrom = shots.First(s => s.Light < baseline * 0.9).Ms, downTo = firstDark;
                long upFrom = lastDark, upTo = shots.Where(s => s.Ms > lastDark && s.Light > baseline * 0.9).Select(s => s.Ms).DefaultIfEmpty(-1).First();
                report.Add($"Screen brightness baseline {baseline:0.0}; black from {firstDark} to {lastDark} ms");
                report.Add($"Fade in (90% -> 10%): {downTo - downFrom} ms; fade out (10% -> 90%): {(upTo < 0 ? -1 : upTo - upFrom)} ms");
                bool fadesOut = upTo > 0 && upTo - upFrom >= 300;
                report.Add(firstDark >= 0 && fadesOut ? "OK: the cover goes on, stays black, and comes off with a fade."
                    : "FAIL: the cover did not come off with a fade (or never turned black).");
                File.WriteAllLines(Path.Combine(folder, "flow-cover.csv"), shots.Select(s => $"{s.Ms};{s.Light:0.0}").Prepend("ms;screen"));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                report.Add("FAIL: " + error);
            }
            Finish(folder, report, done);
        });

    private static void Finish(string folder, List<string> report, Action done)
    {
        File.WriteAllLines(Path.Combine(folder, "flow-test.txt"), report);
        done();
    }

    /// <summary>Average brightness of the whole desktop (0-255), sampled sparsely.</summary>
    private static double Light()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        long sum = 0, count = 0;
        for (int y = 0; y < bitmap.Height; y += 31)
            for (int x = 0; x < bitmap.Width; x += 31)
            {
                var pixel = bitmap.GetPixel(x, y);
                sum += (pixel.R + pixel.G + pixel.B) / 3;
                count++;
            }
        return count == 0 ? 0 : (double)sum / count;
    }
}
