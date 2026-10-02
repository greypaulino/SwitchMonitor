using System.IO;
using System.Runtime.InteropServices;

namespace SwitchMonitor.Wpf;

internal static class StartupManager
{
    // Keep the WPF link separate from the released AutoHotkey link.
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SwitchMonitor WPF.lnk");

    internal static bool CanEnable
    {
        get
        {
            string? executable = Environment.ProcessPath;
            if (executable is null || !File.Exists(executable))
                return false;
            string name = Path.GetFileName(executable);
            if (!name.Equals("SwitchMonitor.Wpf.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("SwitchMonitor.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "installed-wpf.flag")))
                return false;
            return true;
        }
    }

    internal static bool IsEnabled()
    {
        if (!File.Exists(ShortcutPath)) return false;
        try
        {
            string? target = ReadTargetAt(ShortcutPath);
            return target is not null && File.Exists(target) &&
                string.Equals(Path.GetFullPath(target), Path.GetFullPath(Environment.ProcessPath!),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return;
        }
        if (!CanEnable)
            throw new InvalidOperationException("Start with Windows is available after installing a stable WPF build.");
        string executable = Environment.ProcessPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        CreateShortcutAt(ShortcutPath, executable, "--background",
            Path.Combine(AppContext.BaseDirectory, "monitor-switch.ico"));
        if (!IsEnabled())
            throw new IOException("Windows did not create a valid Startup shortcut.");
    }

    internal static string? ReadTargetAt(string linkPath)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new NotSupportedException("Windows Script Host is unavailable."))!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(linkPath);
            return (string)((dynamic)link).TargetPath;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static void CreateShortcutAt(string linkPath, string target, string arguments, string icon)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new NotSupportedException("Windows Script Host is unavailable."))!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(linkPath);
            ((dynamic)link).TargetPath = target;
            ((dynamic)link).Arguments = arguments;
            ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(target)!;
            ((dynamic)link).Description = "SwitchMonitor WPF";
            if (File.Exists(icon)) ((dynamic)link).IconLocation = icon;
            ((dynamic)link).Save();
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
