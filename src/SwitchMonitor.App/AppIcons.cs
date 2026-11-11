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

    /// <summary>
    /// The color of a program's pill: the average of the icon's strong colors (greys and see-through pixels count little), and
    /// white or black text, whichever reads better on it. Grey when there is no icon.
    /// </summary>
    public static (Brush Fill, Brush Ink) PillColors(ImageSource? icon)
    {
        byte r = 0x55, g = 0x55, b = 0x55;
        try
        {
            if (icon is BitmapSource bitmap)
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int w = converted.PixelWidth, h = converted.PixelHeight;
                var pixels = new byte[w * h * 4];
                converted.CopyPixels(pixels, w * 4, 0);
                double sr = 0, sg = 0, sb = 0, total = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    double alpha = pixels[i + 3] / 255.0;
                    if (alpha < 0.5) continue;
                    double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                    double spread = (Math.Max(pr, Math.Max(pg, pb)) - Math.Min(pr, Math.Min(pg, pb))) / 255.0;
                    double weight = alpha * (0.05 + spread * spread);   // vivid pixels decide
                    sr += pr * weight; sg += pg * weight; sb += pb * weight; total += weight;
                }
                if (total > 0) { r = (byte)(sr / total); g = (byte)(sg / total); b = (byte)(sb / total); }
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { }
        // Never too dark: the pill must sit well on the dark window.
        double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        if (luminance < 0.22)
        {
            r = (byte)Math.Min(255, r + 50); g = (byte)Math.Min(255, g + 50); b = (byte)Math.Min(255, b + 50);
            luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
        }
        // A little darker and see-through (40%): the pill tints the card instead of shouting; the text is always white.
        var fill = new SolidColorBrush(Color.FromArgb(0x66, (byte)(r * 0.8), (byte)(g * 0.8), (byte)(b * 0.8)));
        fill.Freeze();
        return (fill, Brushes.White);
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
