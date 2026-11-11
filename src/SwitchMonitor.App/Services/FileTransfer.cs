using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.App.Services;

/// <summary>A device of the group that can receive a file or an image: a computer or a phone connected now.</summary>
public sealed record TransferTarget(string Id, string Name, bool IsPhone, Func<JsonObject, Task<bool>> Send);

/// <summary>
/// Files and images between the devices of the group. Everything travels as numbered pieces ("blob" messages of at most 512 KB, so no
/// message is big): an image is for the clipboard (needs Shared clipboard on here); a file is saved in Downloads\SwitchMonitor after the
/// person here says yes. Only devices already signed in to the group, or paired, can send.
/// </summary>
public sealed class FileTransfer
{
    public const int Piece = 512 * 1024;
    // Roomy limits: they only stop a runaway sender, not a normal transfer.
    public const long MaxFile = 10L * 1024 * 1024 * 1024;
    public const int MaxImage = 100 * 1024 * 1024;
    public const long MaxClipFiles = 500L * 1024 * 1024;
    public const long MaxPixels = 16384L * 16384;
    public const int MaxTransfers = 32;
    public const long MaxImageMemory = 1024L * 1024 * 1024;
    public const long SpaceToKeepFree = 512L * 1024 * 1024;
    private long imageMemory;

    private sealed class Incoming
    {
        public Task Work = Task.CompletedTask;
        public bool Rejected;
        public FileStream? File;
        public MemoryStream? Image;
        public string Path = "";
        public string From = "";
        public string SetId = "";
        public int SetCount;
        public long Received;
        public long Counted;
        public DateTime Last = DateTime.UtcNow;
    }

    private readonly MainViewModel main;
    private readonly ICoreLog log;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentDictionary<string, Incoming> incoming = new();
    private readonly ConcurrentDictionary<string, List<string>> sets = new();
    public static string ClipFolder => Path.Combine(Path.GetTempPath(), "SwitchMonitor-clip");

    public FileTransfer(MainViewModel main, ICoreLog log)
    {
        this.main = main;
        this.log = log;
        try   // copied files of earlier days
        {
            if (Directory.Exists(ClipFolder))
                foreach (var d in Directory.GetDirectories(ClipFolder).Where(d => DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromDays(1))) Directory.Delete(d, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Where received files go.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SwitchMonitor");

    // ---- Sending ----

    /// <summary>The computers and phones that can receive something right now.</summary>
    public IReadOnlyList<TransferTarget> Targets()
    {
        var list = new List<TransferTarget>();
        foreach (var client in main.Peers?.ConnectedClients ?? [])
            list.Add(new TransferTarget(client.Computer.DeviceId ?? client.Computer.Id, client.Computer.Name, false, o => client.SendAsync(o)));
        if (main.Phone is { } phone)
            foreach (var p in phone.PhonesOnline)
            {
                string id = p.Id;
                list.Add(new TransferTarget(id, p.Name, true, o => phone.SendToPhoneAsync(id, o)));
            }
        return list;
    }

    /// <summary>Sends a file; returns null when it went, or the reason it did not.</summary>
    public async Task<string?> SendFileAsync(TransferTarget to, string path, IProgress<double>? progress = null)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return "That file cannot be read."; }
        if (!info.Exists) return "That is not a file (folders are not sent).";
        if (info.Length > MaxFile) return $"That file is bigger than {MaxFile / (1L << 30)} GB.";
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            return await SendStreamAsync(to, "file", info.Name, stream, info.Length, progress);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Write(LogLevel.Debug, $"File transfer: {path} could not be read: {error.Message}");
            return "That file cannot be read.";
        }
    }

    /// <summary>Sends a clipboard image (PNG) to every device that can receive it.</summary>
    public async Task SendImageToAllAsync(byte[] png)
    {
        foreach (var target in Targets())
        {
            using var stream = new MemoryStream(png, false);
            await SendStreamAsync(target, "image", "clipboard.png", stream, png.Length, null);
        }
    }

    /// <summary>Files copied on this computer (Ctrl+C in Explorer): they go to the other computers, which put them on their clipboard to paste.</summary>
    public async Task SendClipFilesAsync(IReadOnlyList<string> paths)
    {
        string setId = Guid.NewGuid().ToString("N");
        foreach (var target in Targets().Where(t => !t.IsPhone))
            foreach (string path in paths)
            {
                try
                {
                    var info = new FileInfo(path);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
                    await SendStreamAsync(target, "clipfile", info.Name, stream, info.Length, null,
                        new JsonObject { ["setId"] = setId, ["setCount"] = paths.Count });
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
    }

    private async Task<string?> SendStreamAsync(TransferTarget to, string purpose, string name, Stream data, long length, IProgress<double>? progress, JsonObject? extra = null)
    {
        string id = Guid.NewGuid().ToString("N");
        int total = (int)Math.Max(1, (length + Piece - 1) / Piece);
        var buffer = new byte[Piece];
        for (int index = 0; index < total; index++)
        {
            int filled = 0;
            while (filled < buffer.Length)
            {
                int n = await data.ReadAsync(buffer.AsMemory(filled));
                if (n == 0) break;
                filled += n;
            }
            var message = new JsonObject
            {
                ["type"] = "blob", ["id"] = id, ["purpose"] = purpose, ["name"] = name, ["size"] = length,
                ["index"] = index, ["total"] = total, ["data"] = Convert.ToBase64String(buffer, 0, filled),
                ["from"] = DeviceIdentity.Id, ["fromName"] = Environment.MachineName,
            };
            if (extra is not null) foreach (var (key, value) in extra) message[key] = value?.DeepClone();
            if (!await to.Send(message)) return $"{to.Name} is not reachable.";
            progress?.Report((index + 1) / (double)total);
        }
        return null;
    }

    // ---- Receiving ----

    /// <summary>One piece arrived (from the server thread): pieces of one transfer are handled one after the other.</summary>
    public void OnBlob(JsonElement message)
    {
        string id = Str(message, "id");
        if (id.Length == 0 || id.Length > 64) return;
        PurgeStale();
        if (!incoming.ContainsKey(id) && incoming.Count >= MaxTransfers) return;
        var transfer = incoming.GetOrAdd(id, _ => new Incoming());
        lock (transfer) transfer.Work = transfer.Work.ContinueWith(_ => Process(id, transfer, message), TaskScheduler.Default).Unwrap();
    }

    private static string Str(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;

    private async Task Process(string id, Incoming t, JsonElement message)
    {
        try
        {
            if (t.Rejected) return;
            t.Last = DateTime.UtcNow;
            string purpose = Str(message, "purpose");
            int index = (int)Num(message, "index");
            int total = (int)Num(message, "total");
            long size = Num(message, "size");
            byte[] bytes = Convert.FromBase64String(Str(message, "data"));
            bool last = index >= total - 1;

            if (purpose == "image")
            {
                if (!main.Settings.SharedClipboard || size > MaxImage) { Drop(id, t); return; }
                t.Image ??= new MemoryStream();
                t.Image.Write(bytes);
                t.Counted += bytes.Length;
                if (t.Image.Length > MaxImage || Interlocked.Add(ref imageMemory, bytes.Length) > MaxImageMemory) { Drop(id, t); return; }
                if (last)
                {
                    var png = t.Image.ToArray();
                    Drop(id, t);
                    await dispatcher.InvokeAsync(() => main.Clipboard?.ApplyImage(png));
                }
                return;
            }

            bool clip = purpose == "clipfile";
            if (purpose != "file" && !clip) { Drop(id, t); return; }
            string name = CleanName(Str(message, "name"));
            if (clip && index == 0)
            {
                // Files copied on another computer: kept in a temporary folder and offered on this clipboard once all of them are here.
                if (!main.Settings.SharedClipboard || size > MaxClipFiles || !HasSpace(ClipFolder, size)) { t.Rejected = true; return; }
                t.SetId = CleanName(Str(message, "setId"));
                t.SetCount = (int)Math.Clamp(Num(message, "setCount"), 1, 50);
                string dir = Path.Combine(ClipFolder, t.SetId);
                Directory.CreateDirectory(dir);
                t.Path = Path.Combine(dir, name);
                for (int n = 2; File.Exists(t.Path); n++) t.Path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
                t.File = new FileStream(t.Path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            }
            else if (index == 0)
            {
                // Only devices of the group (signed in) or paired ones get here, so nothing is asked: the person is only told.
                t.From = Str(message, "fromName");
                if (size > MaxFile) { t.Rejected = true; return; }
                if (!HasSpace(Folder, size))
                {
                    t.Rejected = true;
                    dispatcher.BeginInvoke(() => main.Notify?.Invoke("SwitchMonitor", Loc.T($"Not enough free space to receive {name}."), () => { }));
                    return;
                }
                // A program or script can run code: only those are asked about (everything else from your devices is just saved).
                if (IsProgram(name))
                {
                    string sender = t.From.Length > 0 ? t.From : "another device";
                    bool yes = await dispatcher.InvokeAsync(() => MessageBox.Show(
                        Loc.T($"{sender} sent you {name}, a program or script that can run code. Save it only if you expected it. Save it?"),
                        "SwitchMonitor", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes).Task;
                    if (!yes) { t.Rejected = true; return; }
                }
                Directory.CreateDirectory(Folder);
                t.Path = UniquePath(name);
                t.File = new FileStream(t.Path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            }
            if (t.File is null) return;
            t.Received += bytes.Length;
            if (t.Received > MaxFile) { Drop(id, t); return; }
            await t.File.WriteAsync(bytes);
            if (last)
            {
                await t.File.DisposeAsync();
                t.File = null;
                File.Move(t.Path + ".part", t.Path, true);
                MarkDownloaded(t.Path);
                string finished = t.Path;
                if (clip)
                {
                    string setId = t.SetId;
                    int count = t.SetCount;
                    Drop(id, t);
                    var list = sets.GetOrAdd(setId, _ => []);
                    bool all;
                    lock (list) { list.Add(finished); all = list.Count >= count; }
                    if (all)
                    {
                        sets.TryRemove(setId, out _);
                        var paths = list.ToArray();
                        await dispatcher.InvokeAsync(() => main.Clipboard?.ApplyFiles(paths));
                    }
                    return;
                }
                Drop(id, t);
                string who = t.From.Length > 0 ? t.From : "another device";
                dispatcher.BeginInvoke(() =>
                {
                    string shown = Path.GetFileName(finished);
                    main.Report(Loc.T($"Received {shown}: it is in Downloads\\SwitchMonitor."), true);
                    main.Notify?.Invoke("SwitchMonitor", Loc.T($"{who} sent you {shown}."), () =>
                    {
                        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{finished}\""); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    });
                });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            log.Write(LogLevel.Debug, $"File transfer: a piece could not be handled: {error.Message}");
            Drop(id, t);
        }
    }

    private void Drop(string id, Incoming t)
    {
        t.Rejected = true;
        incoming.TryRemove(id, out _);
        if (t.Counted > 0) { Interlocked.Add(ref imageMemory, -t.Counted); t.Counted = 0; }
        try
        {
            t.Image?.Dispose();
            if (t.File is not null)
            {
                string part = t.Path + ".part";
                t.File.Dispose();
                t.File = null;
                File.Delete(part);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void PurgeStale()
    {
        foreach (var (id, t) in incoming.ToArray())
            if (DateTime.UtcNow - t.Last > TimeSpan.FromMinutes(5)) Drop(id, t);
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
          ".lnk", ".url", ".reg", ".jar", ".cpl", ".msc", ".dll", ".sys", ".gadget", ".application", ".appref-ms", ".iso", ".img", ".vhd", ".vhdx" };

    private static bool IsProgram(string name) => ProgramExtensions.Contains(Path.GetExtension(name));

    private static string CleanName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "file";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name).TrimEnd(' ', '.'))) name = "_" + name;   // CON, NUL, COM1... are devices, not files
        return name.Length > 120 ? name[..120] : name;
    }

    /// <summary>There is room for <paramref name="size"/> bytes (and some to spare) where the file is going.</summary>
    private static bool HasSpace(string folder, long size)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace > size + SpaceToKeepFree; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The "downloaded from the Internet/network" mark, so Windows (SmartScreen, Office) treats the file with care.</summary>
    private static void MarkDownloaded(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException) { }
    }

    private static string UniquePath(string name)
    {
        string path = Path.Combine(Folder, name);
        for (int n = 2; File.Exists(path) || File.Exists(path + ".part"); n++)
            path = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        return path;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    /// <summary>A hash of the pixels, to tell whether an image is the one just applied.</summary>
    internal static byte[] PixelHash(System.Windows.Media.Imaging.BitmapSource image)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return SHA256.HashData(pixels);
    }
}
