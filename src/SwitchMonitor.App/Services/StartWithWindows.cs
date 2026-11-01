using Microsoft.Win32;

namespace SwitchMonitor.App.Services;

/// <summary>
/// "Start with Windows": a value in the user's Run key (no administrator rights needed). It starts this exe with
/// --startup, which keeps the window closed (the app waits in the notification area).
/// </summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SwitchMonitor";
    public const string StartupArgument = "--startup";

    private static string Command => $"\"{Environment.ProcessPath}\" {StartupArgument}";

    /// <summary>True when Windows starts this copy of the app at sign-in.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Another copy (another folder) is registered instead of this one.</summary>
    public static string? OtherCopy
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase) ? value : null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
