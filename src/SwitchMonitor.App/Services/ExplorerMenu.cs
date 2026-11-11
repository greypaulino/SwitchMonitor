using System.IO;
using Microsoft.Win32;

namespace SwitchMonitor.App.Services;

/// <summary>
/// "Send with SwitchMonitor" in the right-click menu of a file in Windows (for this user only, no administrator needed): a submenu
/// with the devices of the group that are connected now, and "Choose device…". A device starts this program with --send-to and the
/// file; the running instance gathers the files and sends them. Windows 11 shows it under "Show more options".
/// </summary>
public static class ExplorerMenu
{
    public const string Argument = "--send";
    public const string DeviceArgument = "--send-to";
    private const string Key = @"Software\Classes\*\shell\SwitchMonitor.Send";

    public sealed record Entry(string? Device, string Path);

    /// <summary>Writes the menu (or removes it when <paramref name="on"/> is false) with these devices in the submenu.</summary>
    public static void Apply(bool on, IEnumerable<(string Id, string Name)>? devices = null)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Key, false);
            if (!on || Environment.ProcessPath is not { } exe) return;
            using var verb = Registry.CurrentUser.CreateSubKey(Key);
            verb.SetValue("MUIVerb", Loc.T("Send with SwitchMonitor"));
            verb.SetValue("Icon", $"\"{exe}\",0");
            verb.SetValue("SubCommands", "");
            int n = 0;
            foreach (var (id, name) in devices ?? [])
            {
                using var item = verb.CreateSubKey($@"shell\{n++:00}device");
                item.SetValue("MUIVerb", name.Replace("&", "&&"));
                using var command = item.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" {DeviceArgument} \"{id}\" \"%1\"");
            }
            using var choose = verb.CreateSubKey($@"shell\{n:00}choose");
            choose.SetValue("MUIVerb", Loc.T("Choose device…"));
            using var chooseCommand = choose.CreateSubKey("command");
            chooseCommand.SetValue("", $"\"{exe}\" {Argument} \"%1\"");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
    }

    /// <summary>Files named after --send (any device) or --send-to (that device) in the command line.</summary>
    public static List<Entry> FilesIn(string[] args)
    {
        var files = new List<Entry>();
        string? device = null;
        int at = Array.FindIndex(args, a => a.Equals(Argument, StringComparison.OrdinalIgnoreCase) || a.Equals(DeviceArgument, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return files;
        var rest = args.Skip(at + 1).ToList();
        if (args[at].Equals(DeviceArgument, StringComparison.OrdinalIgnoreCase) && rest.Count > 0) { device = rest[0]; rest.RemoveAt(0); }
        foreach (string a in rest)
            if (!a.StartsWith("--") && File.Exists(a)) files.Add(new Entry(device, a));
        return files;
    }

    // The launches of the verb (one per file) leave their file here; the running instance reads them together.
    private static string Inbox => Path.Combine(Core.AppPaths.DataDirectory, "send-inbox.txt");

    public static void Queue(IEnumerable<Entry> files)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.AppendAllLines(Inbox, files.Select(f => (f.Device ?? "") + "\t" + f.Path)); return; }
            catch (IOException) { Thread.Sleep(30); }
        }
    }

    public static List<Entry> Take()
    {
        var files = new List<Entry>();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(Inbox)) return files;
                foreach (string line in File.ReadAllLines(Inbox))
                {
                    int tab = line.IndexOf('\t');
                    if (tab < 0) continue;
                    string path = line[(tab + 1)..];
                    if (File.Exists(path)) files.Add(new Entry(tab == 0 ? null : line[..tab], path));
                }
                File.Delete(Inbox);
                return files.Distinct().ToList();
            }
            catch (IOException) { Thread.Sleep(30); }
        }
        return files;
    }
}
