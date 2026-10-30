using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.App.Automation;

/// <summary>A program installed for Windows: the name of its Start menu entry and the .exe it starts.</summary>
public sealed record InstalledApp(string Name, string Exe, string Path);

/// <summary>
/// The programs of the Start menu (all users and this user), read from their shortcuts: each one that starts an .exe,
/// without uninstallers, help files and duplicates. Read once in the background, then kept.
/// </summary>
public static class InstalledApps
{
    private static Task<IReadOnlyList<InstalledApp>>? all;

    public static Task<IReadOnlyList<InstalledApp>> AllAsync() => all ??= Task.Run(Read);

    private static IReadOnlyList<InstalledApp> Read()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var found = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        string own = SceneStore.AppName(Environment.ProcessPath ?? "");
        foreach (string folder in folders.Where(f => f.Length > 0 && Directory.Exists(f)))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string link in links)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(link);
                if (IsNoise(name)) continue;
                string? target = TargetOf(link);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                string exe = SceneStore.AppName(target);
                if (exe == own || exe is "explorer.exe" || IsNoise(exe)) continue;
                found.TryAdd(exe, new InstalledApp(name, exe, target));
            }
        }
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsNoise(string name)
    {
        string value = name.ToLowerInvariant();
        return value.Contains("uninstall") || value.Contains("unins0") || value.Contains("desinstal") ||
               value.Contains("readme") || value.Contains("help") || value.Contains("ayuda");
    }

    /// <summary>The file a .lnk starts, or null (broken, a web address, an app of the Store).</summary>
    private static string? TargetOf(string link)
    {
        object? created = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type is null) return null;
            created = Activator.CreateInstance(type);
            if (created is not IShellLinkW shell) return null;
            ((IPersistFile)shell).Load(link, 0);
            var buffer = new StringBuilder(1024);
            shell.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            string path = Environment.ExpandEnvironmentVariables(buffer.ToString());
            return path.Length > 0 ? path : null;
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or InvalidCastException
            or ArgumentException) { return null; }
        finally
        {
            if (created is not null && Marshal.IsComObject(created)) Marshal.ReleaseComObject(created);
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
