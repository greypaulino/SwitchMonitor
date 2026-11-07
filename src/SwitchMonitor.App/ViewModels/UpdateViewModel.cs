using System.Diagnostics;
using System.Net.Http;
using System.Windows.Input;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Settings;
using SwitchMonitor.Core.Updates;

namespace SwitchMonitor.App.ViewModels;

/// <summary>
/// Updates: the app looks at GitHub's latest release (at start, every 6 hours, and on "Check now"), says so when there is
/// a newer one, and installs it with one click. It never installs by itself. The installed edition downloads the signed
/// installer, proves it, keeps a backup and runs it; the portable edition only points to the release; the Store edition
/// is updated by Windows.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    public const string ReleasePage = "https://github.com/greypaulino/SwitchMonitor/releases/latest";

    private readonly MainViewModel main;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ReleaseFeed feed;
    private readonly UpdateStore store = UpdateStore.Load();
    private readonly VersionNumber current;
    private UpdateInfo? available;
    private string status = "";
    private bool busy;
    private double progress;
    private string? justUpdated;

    public UpdateViewModel(MainViewModel main)
    {
        this.main = main;
        feed = new ReleaseFeed(http);
        Edition = UpdateEditions.Detect();
        VersionNumber.TryParse(typeof(UpdateViewModel).Assembly.GetName().Version?.ToString(3), out current);
        status = Edition == UpdateEdition.Packaged ? "Updated by the Microsoft Store." : "";
        CheckCommand = new RelayCommand(_ => _ = CheckAsync(silent: false), _ => Enabled && !busy);
        InstallCommand = new RelayCommand(_ => _ = InstallAsync(), _ => IsAvailable && !busy);
    }

    /// <summary>False for demo displays and tests: nothing is asked of the network.</summary>
    public bool Enabled { get; set; } = true;

    public UpdateEdition Edition { get; }
    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }

    /// <summary>Raised the first time a version is found (the app shows a tray balloon).</summary>
    public event Action<string>? UpdateFound;

    /// <summary>Raised when the installer was started: the app closes so the installer can replace it.</summary>
    public event Action? InstallStarted;

    public string CurrentVersion => current.ToString();
    public bool CanCheck => Enabled && Edition != UpdateEdition.Packaged;
    public bool IsAvailable => available is not null && Edition != UpdateEdition.Packaged;
    public string AvailableVersion => available?.Version.ToString() ?? "";

    public bool AutoCheck
    {
        get => store.AutoCheck;
        set { if (store.AutoCheck == value) return; store.AutoCheck = value; store.Save(); Raise(); }
    }

    public string Status { get => status; private set { if (Set(ref status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => status.Length > 0;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(IsInstalling)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool IsInstalling => busy && IsAvailable;
    public double Progress { get => progress; private set => Set(ref progress, value); }

    /// <summary>"Install" (installed edition) or "Download" (portable: opens the release page).</summary>
    public string InstallLabel => Edition == UpdateEdition.Installed ? "Install and restart" : "Download";

    public string BannerText => Edition == UpdateEdition.Installed
        ? $"SwitchMonitor {AvailableVersion} is available."
        : $"SwitchMonitor {AvailableVersion} is available. This is the portable edition: download it from the release page.";

    public string LastCheckText => store.LastCheck is { } when ? $"Last checked {Ago(DateTimeOffset.Now - when)}." : "Not checked yet.";

    public string? JustUpdated { get => justUpdated; private set { if (Set(ref justUpdated, value)) Raise(nameof(HasJustUpdated)); } }
    public bool HasJustUpdated => justUpdated is not null;

    private static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(2) ? "just now" : span < TimeSpan.FromHours(2) ? $"{(int)span.TotalMinutes} minutes ago"
        : span < TimeSpan.FromDays(2) ? $"{(int)span.TotalHours} hours ago" : $"{(int)span.TotalDays} days ago";

    /// <summary>True when automatic checking is on and the last check is older than <see cref="CheckEvery"/>.</summary>
    public bool IsDue => Enabled && CanCheck && store.AutoCheck && (store.LastCheck is null || DateTimeOffset.Now - store.LastCheck > CheckEvery);

    /// <summary>After an update the installer leaves a note: tell the person it worked (once).</summary>
    public void AnnounceCompletedUpdate()
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDirectory, "update-complete.ini");
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            File.Delete(path);
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
            if (line is not null && VersionNumber.TryParse(line[8..], out var version) && version == current)
            {
                JustUpdated = $"SwitchMonitor {current} installed successfully.";
                main.Report(JustUpdated, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public async Task CheckAsync(bool silent)
    {
        if (!CanCheck || busy) return;
        IsBusy = true;
        if (!silent) Status = "Checking for updates…";
        try
        {
            var result = await feed.CheckAsync(current, store.ETag).ConfigureAwait(true);
            if (result.Status != UpdateCheckStatus.Failed) store.LastCheck = DateTimeOffset.Now;
            store.ETag = result.Status == UpdateCheckStatus.UpToDate ? result.ETag : null;   // only "nothing new" may be answered with 304
            store.Save();
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    bool first = store.NotifiedVersion != result.Update!.Version.ToString();
                    available = result.Update;
                    Status = $"Version {AvailableVersion} is available.";
                    Notify();
                    if (first)
                    {
                        store.NotifiedVersion = AvailableVersion;
                        store.Save();
                        UpdateFound?.Invoke($"SwitchMonitor {AvailableVersion} is available. Open SwitchMonitor to install it.");
                    }
                    break;
                case UpdateCheckStatus.Failed:
                    if (!silent) Status = result.Error ?? "Could not check for updates.";   // a silent check that fails says nothing
                    break;
                case UpdateCheckStatus.NoReleases:
                    Status = silent ? Status : "No published releases yet.";
                    break;
                default:
                    available = null;
                    Status = $"SwitchMonitor is up to date (version {CurrentVersion}).";
                    Notify();
                    break;
            }
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(LastCheckText));
        }
    }

    private void Notify()
    {
        Raise(nameof(IsAvailable)); Raise(nameof(AvailableVersion)); Raise(nameof(BannerText)); Raise(nameof(InstallLabel)); Raise(nameof(IsInstalling));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>One click: download, prove, back up, install, close. The portable edition opens the release page instead.</summary>
    public async Task InstallAsync()
    {
        if (available is not { } update || busy) return;
        if (Edition != UpdateEdition.Installed)
        {
            Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true });
            return;
        }
        if (main.IsApplyingProfile)
        {
            Status = "A profile change is running; try again in a moment.";
            return;
        }
        IsBusy = true;
        Progress = 0;
        try
        {
            Status = $"Downloading SwitchMonitor {update.Version}…";
            var downloader = new UpdateDownloader(http, feed);
            var done = await downloader.DownloadAsync(update, Path.Combine(AppPaths.DataDirectory, "updates"),
                new Progress<double>(p => Progress = p)).ConfigureAwait(true);
            Status = "Keeping a backup of your settings…";
            try
            {
                Directory.CreateDirectory(Backup.SafetyDirectory(AppPaths.DataDirectory));
                Backup.Create(AppPaths.DataDirectory, Path.Combine(Backup.SafetyDirectory(AppPaths.DataDirectory), $"before-update-{update.Version}{Backup.Extension}"), CurrentVersion);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { /* a backup is a courtesy: it does not stop the update */ }
            Status = $"Installing SwitchMonitor {update.Version}…";
            Process.Start(new ProcessStartInfo(done.InstallerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = false });
            await Task.Delay(300).ConfigureAwait(true);
            InstallStarted?.Invoke();
        }
        catch (Exception error) when (error is InvalidDataException or HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Status = $"The update could not be installed: {error.Message}";
            main.Report(Status, false);
        }
        finally { IsBusy = false; }
    }
}
