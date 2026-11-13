using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;
using UIA = System.Windows.Automation;

namespace SwitchMonitor.App.Services;

/// <summary>
/// What the phone's Remote tab controls: the program that is playing media right now (Brave on YouTube, Spotify…), found through
/// the Windows media session, so play/pause, next/previous and a jump of 10 s act on it, and the volume is that program's own.
/// With nothing playing the Remote falls back to the media keys and the Windows volume.
/// </summary>
public static class ActiveMedia
{
    private static GlobalSystemMediaTransportControlsSessionManager? manager;

    private static async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            // The one that is playing; else the one Windows considers current (the last used), if it still has something loaded.
            foreach (var s in sessions)
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
            var current = manager.GetCurrentSession();
            return current?.GetPlaybackInfo()?.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused ? current : null;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException)
        {
            manager = null;
            return null;
        }
    }

    /// <summary>Gets the media session manager ready, so <see cref="Snapshot"/> can answer at once.</summary>
    public static async Task PrimeAsync()
    {
        try { manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException) { manager = null; }
    }

    private static long lastAudibleAt;
    private static long pageCheckedAt;
    private static volatile bool pageChecking;
    private static bool? pageMute;   // the video page's own mute: true / false, null when it could not be read

    private static async Task RefreshPageMuteAsync(GlobalSystemMediaTransportControlsSession session, string source)
    {
        string title = "";
        try { title = (await session.TryGetMediaPropertiesAsync())?.Title ?? ""; }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException) { }
        pageMute = title.Length == 0 ? null : BrowserPlayer.ReadMuted(source, title);
    }

    /// <summary>
    /// What the phone's mute key shows: is the program that plays media silenced (its audio muted, Windows muted, or it plays and nothing
    /// comes out for a few seconds: a page muted in its own player), is sound coming out, is it playing.
    /// </summary>
    public static (bool Muted, bool Audible, bool Playing) Snapshot()
    {
        bool master = WindowsVolume.Read() is { Muted: true };
        try
        {
            var current = manager;
            if (current is null) return (master, false, false);
            GlobalSystemMediaTransportControlsSession? pick = null;
            foreach (var s in current.GetSessions())
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { pick = s; break; }
            bool playing = pick is not null;
            if (pick is null && current.GetCurrentSession() is { } last
                && last.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) pick = last;
            if (pick is null) return (master, false, false);
            string source = (pick.SourceAppUserModelId ?? "").ToLowerInvariant();
            var (appMuted, peak) = AppAudio.Read(source);
            long now = Environment.TickCount64;
            // A video page in a browser has its own mute (not the browser's audio): looked at in the background, at most every 1.5 s.
            if (BrowserPlayer.IsBrowser(source) && now - pageCheckedAt > 1500 && !pageChecking)
            {
                pageChecking = true;
                var watched = pick;
                _ = Task.Run(async () => { try { await RefreshPageMuteAsync(watched, source); } finally { pageChecking = false; pageCheckedAt = Environment.TickCount64; } });
            }
            if (peak > 0.0008f) lastAudibleAt = now;
            bool audible = now - lastAudibleAt < 1500;
            bool silentWhilePlaying = playing && now - lastAudibleAt > 3000;
            bool pageMuted = pageMute is { } known && now - pageCheckedAt < 6000 && known;
            return (master || appMuted || pageMuted || (pageMute is null && silentWhilePlaying), audible, playing);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException)
        {
            return (master, false, false);
        }
    }

    /// <summary>play/pause, next, prev, seekfwd, seekback on the active program; false when there is none (the caller uses the media keys).</summary>
    public static async Task<bool> CommandAsync(string key)
    {
        if (await SessionAsync() is not { } session) return false;
        try
        {
            switch (key)
            {
                case "playpause": return await session.TryTogglePlayPauseAsync();
                case "next": return await session.TrySkipNextAsync();
                case "prev": return await session.TrySkipPreviousAsync();
                case "stop": return await session.TryStopAsync();
                case "seekfwd":
                case "seekback":
                {
                    var line = session.GetTimelineProperties();
                    var position = line.Position;
                    if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        position += DateTimeOffset.Now - line.LastUpdatedTime;   // the snapshot is old by now
                    var target = position + TimeSpan.FromSeconds(key == "seekfwd" ? 10 : -10);
                    var max = line.EndTime > line.MaxSeekTime ? line.EndTime : line.MaxSeekTime;
                    if (target < line.MinSeekTime) target = line.MinSeekTime;
                    if (max > TimeSpan.Zero && target > max) target = max;
                    return await session.TryChangePlaybackPositionAsync(target.Ticks);
                }
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    /// <summary>
    /// Volume up / down (±5 %) or mute (toggle) of the program that is playing. False when no program is playing or none of its
    /// audio sessions is found (the caller then changes the Windows volume).
    /// </summary>
    public static async Task<bool> VolumeAsync(string key)
    {
        if (await SessionAsync() is not { } session) { Trace.Write($"media {key}: no session is playing, the Windows volume is used"); return false; }
        string source = (session.SourceAppUserModelId ?? "").ToLowerInvariant();
        if (source.Length == 0) return false;
        // A browser playing a page (YouTube): the page's own volume and mute first, as its player shows them; the browser's audio
        // as a whole when that page is not the one in front in its window.
        if (BrowserPlayer.IsBrowser(source))
        {
            string title = "";
            try { title = (await session.TryGetMediaPropertiesAsync())?.Title ?? ""; }
            catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException) { }
            bool page = title.Length > 0 && await Task.Run(() => BrowserPlayer.Change(source, title, key));
            Trace.Write($"media {key}: {source} \"{title}\" -> {(page ? "the page's own player" : "not found in a window: the browser's audio")}");
            if (page) return true;
        }
        return await Task.Run(() => AppAudio.Change(source, key));
    }

    /// <summary>
    /// The volume and mute of a video page (YouTube) in a Chromium browser, through the page's own player: its slider and mute button
    /// are found with UI Automation (the browser makes its page tree available when asked), the slider takes the focus and gets an
    /// arrow key (5 % each, like the player's own), without bringing the window to the front.
    /// </summary>
    private static class BrowserPlayer
    {
        private static readonly string[] Names = ["brave", "chrome", "msedge", "vivaldi", "opera"];

        public static bool IsBrowser(string source) => Names.Any(source.Contains);

        private static readonly System.Text.RegularExpressions.Regex Unmute = new(
            @"(?i)^(un-?mute|activar sonido|reactivar|ativar som|reativar|activer le son|réactiver|ton ein|stummschaltung aufheben|riattiva|включить звук|取消静音|ミュート解除)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Whether the page's player is muted (its mute button offers to turn the sound on); null when the player is not found.</summary>
        public static bool? ReadMuted(string source, string title)
        {
            try
            {
                string browser = Names.First(source.Contains);
                var windows = UIA.AutomationElement.RootElement.FindAll(UIA.TreeScope.Children,
                    new UIA.PropertyCondition(UIA.AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
                foreach (UIA.AutomationElement window in windows)
                {
                    if (!(window.Current.Name ?? "").Contains(title, StringComparison.CurrentCultureIgnoreCase) || !ProcessIs(window.Current.ProcessId, browser)) continue;
                    foreach (UIA.AutomationElement button in window.FindAll(UIA.TreeScope.Descendants,
                                 new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Button)))
                    {
                        string text = button.Current.Name ?? "";
                        if (text.EndsWith("(m)", StringComparison.Ordinal) || text.EndsWith(" m", StringComparison.Ordinal)) return Unmute.IsMatch(text);
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            return null;
        }

        public static bool Change(string source, string title, string key)
        {
            try
            {
                string browser = Names.First(source.Contains);
                var windows = UIA.AutomationElement.RootElement.FindAll(UIA.TreeScope.Children,
                    new UIA.PropertyCondition(UIA.AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
                foreach (UIA.AutomationElement window in windows)
                {
                    string name = window.Current.Name ?? "";
                    if (!name.Contains(title, StringComparison.CurrentCultureIgnoreCase) || !ProcessIs(window.Current.ProcessId, browser)) continue;
                    // The first request makes the browser build the page's accessibility tree: it may need a moment.
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        if (Act(window, key)) return true;
                        Thread.Sleep(450);
                    }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // UI Automation throws its own kinds (element gone, not enabled, no provider): the browser's audio is used instead.
                Trace.Write($"media {key}: the page could not be reached ({error.GetType().Name}: {error.Message})");
            }
            return false;
        }

        private static bool ProcessIs(int id, string browser)
        {
            try { using var p = Process.GetProcessById(id); return p.ProcessName.Contains(browser, StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        }

        private static bool Act(UIA.AutomationElement window, string key)
        {
            if (key == "mute")
            {
                // The player's mute button is named "Mute (m)" (or the same in the browser's language: it always ends in the key m).
                foreach (UIA.AutomationElement button in window.FindAll(UIA.TreeScope.Descendants,
                             new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Button)))
                {
                    string text = button.Current.Name ?? "";
                    if (!(text.EndsWith("(m)", StringComparison.Ordinal) || text.EndsWith(" m", StringComparison.Ordinal))) continue;
                    if (button.TryGetCurrentPattern(UIA.InvokePattern.Pattern, out object? pattern) && pattern is UIA.InvokePattern invoke)
                    {
                        invoke.Invoke();
                        pageMute = !Unmute.IsMatch(text);   // it was "Mute" (now muted) or "Unmute" (now not): known at once, the next look confirms it
                        pageCheckedAt = Environment.TickCount64;
                        return true;
                    }
                }
                return false;
            }
            foreach (UIA.AutomationElement slider in window.FindAll(UIA.TreeScope.Descendants,
                         new UIA.PropertyCondition(UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.Slider)))
            {
                string text = slider.Current.Name ?? "";
                if (!(text.StartsWith("Vol", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Son", StringComparison.OrdinalIgnoreCase))) continue;   // Volume, Volumen, Volume (pt, fr), Son
                if (window.Current.NativeWindowHandle == 0) return false;
                slider.SetFocus();
                Thread.Sleep(60);
                IntPtr target = RenderWidget((IntPtr)window.Current.NativeWindowHandle);
                if (target == IntPtr.Zero) return false;
                int vk = key == "volup" ? 0x26 : 0x28;
                int scan = key == "volup" ? 0x48 : 0x50;   // the browser reads the scan code too
                PostMessage(target, 0x100, (IntPtr)vk, (IntPtr)(1 | (scan << 16)));
                PostMessage(target, 0x101, (IntPtr)vk, unchecked((IntPtr)(int)(0xC0000001u | ((uint)scan << 16))));
                return true;
            }
            return false;
        }

        private delegate bool EnumProc(IntPtr handle, IntPtr state);

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        /// <summary>The window that receives the keys of a page (the first is enough: the page that has the focus takes them).</summary>
        private static IntPtr RenderWidget(IntPtr top)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(top, (handle, _) =>
            {
                var text = new System.Text.StringBuilder(64);
                GetClassName(handle, text, text.Capacity);
                if (text.ToString() != "Chrome_RenderWidgetHostHWND") return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }
    }

    /// <summary>Per-program volume through the Core Audio sessions of the default playback device.</summary>
    private static class AppAudio
    {
        private static Guid context = Guid.Empty;

        /// <summary>Whether the audio sessions of the program are all muted, and the loudest sound coming out of them now (0..1).</summary>
        public static (bool Muted, float Peak) Read(string source)
        {
            if (source.Length == 0) return (false, 0f);
            bool any = false, allMuted = true;
            float peak = 0f;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return (false, 0f);
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, 0x17, IntPtr.Zero, out object managerObject) != 0) return (false, 0f);
                    var sessions = ((IAudioSessionManager2)managerObject).GetSessionEnumerator(out var list) == 0 ? list : null;
                    if (sessions is null || sessions.GetCount(out int count) != 0) return (false, 0f);
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var control) != 0 || control is not IAudioSessionControl2 two) continue;
                        if (two.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                        string name;
                        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                        if (name.Length < 3 || !source.Contains(name)) continue;
                        if (control is ISimpleAudioVolume volume && volume.GetMute(out bool muted) == 0) { any = true; allMuted &= muted; }
                        if (control is IAudioMeterInformation meter && meter.GetPeakValue(out float value) == 0) peak = Math.Max(peak, value);
                    }
                }
                finally { Marshal.ReleaseComObject(enumerator); }
            }
            catch (Exception error) when (error is COMException or InvalidCastException) { return (false, 0f); }
            return (any && allMuted, peak);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }

        public static bool Change(string source, string key)
        {
            var found = new List<ISimpleAudioVolume>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null) return false;
                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, 0x17, IntPtr.Zero, out object managerObject) != 0) return false;
                    var sessions = ((IAudioSessionManager2)managerObject).GetSessionEnumerator(out var list) == 0 ? list : null;
                    if (sessions is null || sessions.GetCount(out int count) != 0) return false;
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var control) != 0 || control is not IAudioSessionControl2 two) continue;
                        if (two.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                        string name;
                        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                        // The media session names the program as its app id ("Spotify.exe", "Brave", "SpotifyAB.SpotifyMusic_…!Spotify").
                        if (name.Length < 3 || !source.Contains(name)) continue;
                        if (control is ISimpleAudioVolume volume) found.Add(volume);
                    }
                }
                finally { Marshal.ReleaseComObject(enumerator); }

                if (found.Count == 0) return false;
                foreach (var volume in found)
                {
                    if (key == "mute")
                    {
                        volume.GetMute(out bool muted);
                        volume.SetMute(!muted, ref context);
                    }
                    else
                    {
                        volume.GetMasterVolume(out float level);
                        float next = Math.Clamp(level + (key == "volup" ? 0.05f : -0.05f), 0f, 1f);
                        volume.SetMasterVolume(next, ref context);
                        if (next > 0) volume.SetMute(false, ref context);
                    }
                }
                return true;
            }
            catch (Exception error) when (error is COMException or InvalidCastException)
            {
                return false;
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
            [PreserveSig] int GetGroupingParam(out Guid grouping);
            [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint processId);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
