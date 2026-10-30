using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwitchMonitor.App;

/// <summary>Icon and friendly name of a program ("Notepad" for notepad.exe), read from the file and cached.</summary>
public static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? IconOf(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        lock (icons)
            if (icons.TryGetValue(fullPath, out var known)) return known;
        ImageSource? image = null;
        try
        {
            if (File.Exists(fullPath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(fullPath);
                if (icon is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { }
        lock (icons) icons[fullPath] = image;   // also read in the background (list of installed programs)
        return image;
    }

    /// <summary>The program's description ("Notepad"), or its file name without ".exe".</summary>
    public static string NameOf(string exe, string? fullPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath) &&
                FileVersionInfo.GetVersionInfo(fullPath).FileDescription is { Length: > 0 } description &&
                description.Length <= 40)
                return description.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileNotFoundException) { }
        return Path.GetFileNameWithoutExtension(exe);
    }
}
