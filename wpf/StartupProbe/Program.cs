using SwitchMonitor.Wpf;

string target = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
string shortcut = Path.Combine(Path.GetTempPath(),
    "SwitchMonitor-startup-probe-" + Guid.NewGuid().ToString("N") + ".lnk");
try
{
    StartupManager.CreateShortcutAt(shortcut, target, "--background", "");
    string? readBack = StartupManager.ReadTargetAt(shortcut);
    if (!string.Equals(Path.GetFullPath(readBack ?? ""), Path.GetFullPath(target),
        StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The shortcut target did not match the executable.");
    Console.WriteLine("Shortcut creation/read-back: PASS (temporary file only)");
}
finally { if (File.Exists(shortcut)) File.Delete(shortcut); }
