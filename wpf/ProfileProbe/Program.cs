using SwitchMonitor.Wpf;

if (args is ["--selftest-shortcuts"])
{
    var profile = new Preferences
    {
        GlobalShortcuts = new() { ["cycle"] = "Ctrl+Alt+M" },
        PortShortcuts = new()
        {
            ["monitor-A"] = new() { [17] = "Ctrl+Alt+1" },
            ["monitor-B"] = new() { [15] = "Ctrl+Alt+2" }
        }
    };
    ShortcutProfileValidator.Validate(profile);
    profile.PortShortcuts["monitor-B"][15] = "Ctrl+Alt+1";
    try { ShortcutProfileValidator.Validate(profile); throw new Exception("Duplicate shortcut was accepted."); }
    catch (System.IO.InvalidDataException) { }
    profile.PortShortcuts["monitor-B"][15] = "Ctrl+Alt+Q";
    profile.GlobalShortcuts["settings"] = "Ctrl+Alt+Q";
    try { ShortcutProfileValidator.Validate(profile); throw new Exception("Global shortcut collision was accepted."); }
    catch (System.IO.InvalidDataException) { }
    Console.WriteLine("Shortcut validation: PASS (cross-monitor and global conflicts rejected)");
    return;
}

string source = args.Length > 0 ? args[0] : LegacySettingsImporter.InstalledPath;
IReadOnlyList<PhysicalMonitor> monitors = [];
try
{
    monitors = MonitorService.Enumerate();
    foreach (var monitor in monitors)
        try { monitor.ReadInputs(); } catch { }
    var preview = LegacySettingsImporter.Preview(source, Preferences.Load(), monitors);
    Console.WriteLine(preview.Summary);
    Console.WriteLine("Read-only preview: no settings were changed.");
}
catch (Exception error)
{
    Console.Error.WriteLine("Import preview rejected: " + error.Message);
    Environment.ExitCode = 1;
}
finally { foreach (var monitor in monitors) monitor.Dispose(); }
