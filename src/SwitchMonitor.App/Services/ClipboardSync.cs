using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The shared clipboard: text copied on this computer goes to the other computers of the group and to the phones, and theirs comes
/// here. Every device sends to all the others directly (nothing is passed on), so a text never loops; each text carries an id and the
/// last text applied here is not sent back. Off until turned on in Settings > Devices (a clipboard can hold a password), and only the
/// devices of the group, already signed in, can send to it.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int MaxChars = 100_000;

    private readonly MainViewModel main;
    private readonly ICoreLog log;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HwndSource source;
    private readonly Queue<string> seen = new();
    private string lastApplied = "";

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr window);

    public ClipboardSync(MainViewModel main, ICoreLog log)
    {
        this.main = main;
        this.log = log;
        // A window with no screen of its own, only to hear "the clipboard changed".
        source = new HwndSource(new HwndSourceParameters("SwitchMonitorClipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(Hook);
        AddClipboardFormatListener(source.Handle);
    }

    private bool Enabled => main.Settings.SharedClipboard;

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate) dispatcher.BeginInvoke(Changed, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    /// <summary>Something was copied here: its text goes to every other device of the group.</summary>
    private void Changed()
    {
        if (!Enabled || Excluded()) return;
        string? text = ReadText();
        if (string.IsNullOrEmpty(text)) { if (!SendFiles()) SendImage(); return; }
        if (text == lastApplied || text.Length > MaxChars) return;
        lastApplied = text;
        var message = new JsonObject { ["type"] = "clip", ["id"] = Guid.NewGuid().ToString("N"), ["text"] = text, ["from"] = DeviceIdentity.Id };
        Remember(message["id"]!.GetValue<string>());
        foreach (var computer in main.Peers?.ConnectedClients ?? []) _ = computer.SendAsync(message);
        if (main.Phone is { } phone) _ = phone.SendToPhonesAsync(message);
    }

    /// <summary>A device of the group copied text: it goes onto this computer's clipboard (when this one has the switch on).</summary>
    public void OnClip(JsonElement message)
    {
        if (!Enabled) return;
        string id = message.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        string text = message.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (text.Length == 0 || text.Length > MaxChars || id.Length == 0) return;
        dispatcher.BeginInvoke(() =>
        {
            if (!Remember(id) || text == lastApplied) return;   // already seen
            lastApplied = text;
            Write(text);
        });
    }

    /// <summary>
    /// A program (a password manager, for one) can mark what it copied as not to be shared or kept: those clips are never sent.
    /// </summary>
    private static bool Excluded()
    {
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream history && history.Length >= 4)
            {
                var four = new byte[4];
                history.Position = 0;
                history.ReadExactly(four);
                if (BitConverter.ToInt32(four) == 0) return true;
            }
            if (Clipboard.GetData("CanUploadToCloudClipboard") is MemoryStream cloud && cloud.Length >= 4)
            {
                var four = new byte[4];
                cloud.Position = 0;
                cloud.ReadExactly(four);
                if (BitConverter.ToInt32(four) == 0) return true;
            }
        }
        catch (Exception error) when (error is COMException or IOException or InvalidOperationException) { }
        return false;
    }

    // ---- Images: the same, as numbered pieces (see FileTransfer) ----

    private byte[] lastImage = [];
    private DateTime ignoreUntil;

    /// <summary>An image was copied here: it goes as a PNG to the other devices (not when it is the one just applied from another).</summary>
    private void SendImage()
    {
        if (DateTime.UtcNow < ignoreUntil || main.Transfers is not { } transfers) return;
        System.Windows.Media.Imaging.BitmapSource? image = null;
        for (int attempt = 0; attempt < 5 && image is null; attempt++)
        {
            try { if (!Clipboard.ContainsImage()) return; image = Clipboard.GetImage(); }
            catch (COMException) { Thread.Sleep(40); }
        }
        if (image is null) return;
        byte[] hash = FileTransfer.PixelHash(image);
        if (hash.AsSpan().SequenceEqual(lastImage)) return;
        lastImage = hash;
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] png = stream.ToArray();
        if (png.Length > FileTransfer.MaxImage) return;
        _ = Task.Run(() => transfers.SendImageToAllAsync(png));
    }

    /// <summary>An image of another device arrived whole: it goes onto this clipboard (on the UI thread).</summary>
    public void ApplyImage(byte[] png)
    {
        if (!Enabled) return;
        try
        {
            // The size in pixels is read first: a small file can unpack into an enormous picture.
            var header = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
            if (header.Frames.Count == 0 || (long)header.Frames[0].PixelWidth * header.Frames[0].PixelHeight > FileTransfer.MaxPixels) return;
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            lastImage = FileTransfer.PixelHash(frame);
            ignoreUntil = DateTime.UtcNow.AddSeconds(2);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetImage(frame); return; }
                catch (COMException) { Thread.Sleep(40); }
            }
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            log.Write(LogLevel.Debug, "Shared clipboard: an image from another device could not be read.");
        }
    }

    // ---- Files: copy in Explorer here, paste in Explorer there ----

    private string lastFiles = "";

    /// <summary>Files were copied here: they go to the other computers (small sets only; bigger ones go with "Send files"). False when it was not a file copy.</summary>
    private bool SendFiles()
    {
        System.Collections.Specialized.StringCollection? list = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (!Clipboard.ContainsFileDropList()) return false; list = Clipboard.GetFileDropList(); break; }
            catch (COMException) { Thread.Sleep(40); }
        }
        if (list is null || main.Transfers is not { } transfers) return false;
        var files = list.Cast<string>().Where(File.Exists).Take(50).ToList();
        if (files.Count == 0) return true;   // only folders: not sent
        string signature = string.Join("|", files.Select(f => Path.GetFileName(f) + new FileInfo(f).Length));
        if (DateTime.UtcNow < ignoreUntil || signature == lastFiles) return true;
        lastFiles = signature;
        long total = files.Sum(f => new FileInfo(f).Length);
        if (total > FileTransfer.MaxClipFiles)
        {
            main.Notify?.Invoke("SwitchMonitor", Loc.T("Those files are too big to copy to your other computers (500 MB). Use Send files."), () => { });
            return true;
        }
        if (transfers.Targets().All(t => t.IsPhone)) return true;
        _ = Task.Run(() => transfers.SendClipFilesAsync(files));
        return true;
    }

    /// <summary>Files copied on another computer arrived whole: they go onto this clipboard, ready to paste (on the UI thread).</summary>
    public void ApplyFiles(string[] paths)
    {
        if (!Enabled) return;
        var list = new System.Collections.Specialized.StringCollection();
        list.AddRange(paths);
        lastFiles = string.Join("|", paths.Select(f => Path.GetFileName(f) + new FileInfo(f).Length));
        ignoreUntil = DateTime.UtcNow.AddSeconds(2);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetFileDropList(list); return; }
            catch (COMException) { Thread.Sleep(40); }
        }
    }

    private bool Remember(string id)
    {
        if (seen.Contains(id)) return false;
        seen.Enqueue(id);
        while (seen.Count > 50) seen.Dequeue();
        return true;
    }

    // Another program may hold the clipboard for a moment: try a few times.
    private static string? ReadText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(40); }
        }
        return null;
    }

    private void Write(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (COMException) { Thread.Sleep(40); }
        }
        log.Write(LogLevel.Debug, "Shared clipboard: the clipboard was busy; a text from another device was not applied.");
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(source.Handle);
        source.Dispose();
    }
}
