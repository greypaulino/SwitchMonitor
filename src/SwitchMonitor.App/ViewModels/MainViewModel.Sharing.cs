using System.Net.Http;
using System.Windows.Input;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.ViewModels;

/// <summary>Settings > Startup and sharing: start with Windows, backup, and the shared monitor settings.</summary>
public sealed partial class MainViewModel
{
    private ICommand? exportBackup, importBackup, updateDatabase, exportLearned, importLearned;
    private bool updatingDatabase;

    private static string AppVersion =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "2";

    // ---- Start with Windows ----

    /// <summary>Windows starts the app at sign-in (it waits in the notification area). Saved at once, in the registry.</summary>
    public bool StartWithWindows
    {
        get
        {
            try { return Services.StartWithWindows.IsEnabled; }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
        set
        {
            try
            {
                Services.StartWithWindows.Set(value);
                Report(value ? "SwitchMonitor starts with Windows (in the notification area)." : "SwitchMonitor no longer starts with Windows.", true);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Report($"Start with Windows: {error.Message}", false);
            }
            Raise();
            Raise(nameof(StartWithWindowsHint));
        }
    }

    public string StartWithWindowsHint
    {
        get
        {
            string? other = null;
            try { other = Services.StartWithWindows.OtherCopy; }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            return other is null
                ? "Opens at sign-in in the notification area, ready for its shortcuts."
                : "Another copy of SwitchMonitor starts with Windows now; turning this on uses this copy instead.";
        }
    }

    // ---- Backup ----

    public ICommand ExportBackupCommand => exportBackup ??= new RelayCommand(ExportBackup);
    public ICommand ImportBackupCommand => importBackup ??= new RelayCommand(ImportBackup);

    /// <summary>Raised after a backup was restored: the app starts again with the restored settings.</summary>
    public event Action? RestartRequested;
    /// <summary>The next restart asks Windows for administrator rights (so shared input can reach windows that run as administrator).</summary>
    public bool RestartElevated { get; set; }
    public void RequestRestart(bool elevated) { RestartElevated = elevated; RestartRequested?.Invoke(); }

    private void ExportBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export a backup of SwitchMonitor",
            FileName = $"SwitchMonitor {DateTime.Now:yyyy-MM-dd}{Backup.Extension}",
            Filter = $"SwitchMonitor backup (*{Backup.Extension})|*{Backup.Extension}",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            int files = Backup.Create(AppPaths.DataDirectory, dialog.FileName, AppVersion);
            Report($"Backup saved: {Path.GetFileName(dialog.FileName)} ({files} files: profiles, shortcuts, appearance, learned settings).", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Report($"Backup: {error.Message}", false);
        }
    }

    private void ImportBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a backup of SwitchMonitor",
            Filter = $"SwitchMonitor backup (*{Backup.Extension})|*{Backup.Extension}|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        var answer = System.Windows.MessageBox.Show(
            "Replace every setting of SwitchMonitor (profiles, shortcuts, appearance, learned settings) with the ones in this backup?\n\n" +
            "The current settings are kept in the backups folder first. SwitchMonitor starts again afterwards.",
            "Import backup", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        try
        {
            string safety = Backup.Restore(dialog.FileName, AppPaths.DataDirectory, AppVersion);
            log.Write(LogLevel.Info, $"Backup {dialog.FileName} restored; the previous settings are in {safety}.");
            RestartRequested?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or System.Text.Json.JsonException)
        {
            Report($"Backup: {error.Message}", false);
        }
    }

    // ---- Shared monitor settings ----

    public ICommand UpdateDatabaseCommand => updateDatabase ??= new RelayCommand(_ => _ = UpdateDatabaseAsync(), _ => !updatingDatabase);
    public ICommand ExportLearnedCommand => exportLearned ??= new RelayCommand(ExportLearned);
    public ICommand ImportLearnedCommand => importLearned ??= new RelayCommand(ImportLearned);

    public string DatabaseStatus
    {
        get
        {
            var file = new FileInfo(QuirkDatabase.SharedFilePath);
            return file.Exists
                ? $"Shared settings downloaded on {file.LastWriteTime:d}."
                : "Uses the settings built into this version. Download the shared ones for monitors added since.";
        }
    }

    /// <summary>Downloads the shared monitor settings and their SHA-256, checks them, installs them and lists the displays again.</summary>
    private async Task UpdateDatabaseAsync()
    {
        updatingDatabase = true;
        CommandManager.InvalidateRequerySuggested();
        Report("Downloading the shared monitor settings…", true);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SwitchMonitor/{AppVersion}");
            byte[] content = await http.GetByteArrayAsync(SharedQuirks.DatabaseUrl);
            string hash = await http.GetStringAsync(SharedQuirks.HashUrl);
            int models = SharedQuirks.Install(content, hash);
            log.Write(LogLevel.Info, $"Shared monitor settings installed ({models} models, SHA-256 {SharedQuirks.Hash(content)}).");
            Raise(nameof(DatabaseStatus));
            await RefreshAsync();
            Report($"Shared monitor settings installed: {models} monitor models, checked with SHA-256.", true);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or FormatException
            or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Report($"Shared monitor settings: {error.Message}", false);
        }
        finally
        {
            updatingDatabase = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Saves what was learned for the selected display's model as a .smmonitor file (to keep, or to send to the project).</summary>
    private void ExportLearned()
    {
        if (Selected is not { } monitor) { Report("Select a display first.", false); return; }
        string? file;
        try { file = SharedQuirks.ExportLearned(Core.Learning.LearnedSettingsStore.QuirkId(monitor.Device)); }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Report($"Export: {error.Message}", false);
            return;
        }
        if (file is null)
        {
            Report($"Nothing has been learned for {monitor.Name} yet (Learn settings, next to its name).", false);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Export the learned settings of {monitor.Name}",
            FileName = monitor.Name + SharedQuirks.MonitorExtension,
            Filter = $"SwitchMonitor monitor settings (*{SharedQuirks.MonitorExtension})|*{SharedQuirks.MonitorExtension}",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, file);
            Report($"Learned settings of {monitor.Name} exported to {Path.GetFileName(dialog.FileName)}.", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Report($"Export: {error.Message}", false); }
    }

    /// <summary>Adds the settings of a .smmonitor file to this computer's learned settings and lists the displays again.</summary>
    private void ImportLearned()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import monitor settings",
            Filter = $"SwitchMonitor monitor settings (*{SharedQuirks.MonitorExtension})|*{SharedQuirks.MonitorExtension}|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            int count = SharedQuirks.ImportLearned(File.ReadAllText(dialog.FileName));
            Report($"{count} monitor setting(s) imported.", true);
            _ = RefreshAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            Report($"Import: {error.Message}", false);
        }
    }
}
