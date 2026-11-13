using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The Windows Firewall rule for this program: it lets other devices reach it on home (private) and work (domain) networks only, so on
/// a public network (a café, a hotel) nobody can even try. Windows asks about it the first time and the answer is easy to get wrong;
/// this sets it on purpose, after the person clicks (Windows asks for the administrator's permission to change the firewall).
/// </summary>
public static class FirewallRule
{
    private const string RuleName = "SwitchMonitor (home network)";

    /// <summary>True when the rule exists for this very program file (reading the rules needs no permission).</summary>
    public static bool IsSet()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            var info = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" verbose")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process is null) return false;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            // netsh answers in the language of Windows: only the exit code (a rule was found) and the program path are read.
            return process.ExitCode == 0 && output.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Removes every rule for this program (Windows' own, made when it asked) and adds one for private and domain networks. Asks Windows
    /// for permission; false when the person said no.
    /// </summary>
    /// <summary>Takes the rule out again (asks Windows for permission); false when the person said no.</summary>
    public static async Task<bool> RemoveAsync()
    {
        try
        {
            var info = new ProcessStartInfo("cmd.exe", $"/c \"netsh advfirewall firewall delete rule name=\"{RuleName}\"\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return !IsSet();
        }
        catch (Win32Exception) { return false; }
    }

    public static async Task<bool> SetAsync()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        string commands = $"netsh advfirewall firewall delete rule name=all program=\"{exe}\" & " +
                          $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain protocol=any";
        try
        {
            var info = new ProcessStartInfo("cmd.exe", $"/c \"{commands}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var process = Process.Start(info);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && IsSet();
        }
        catch (Win32Exception) { return false; }   // the permission was refused
    }
}
