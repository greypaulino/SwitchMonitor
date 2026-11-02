using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Settings;

/// <summary>
/// One file with everything the app keeps (profiles, shortcuts, appearance, learned settings, profile pictures, …):
/// a zip of the data folder without the logs and earlier backups, with a small manifest so other zips are refused.
/// </summary>
public static class Backup
{
    public const string Extension = ".smbackup";
    private const string ManifestName = "switchmonitor-backup.json";
    private static readonly string[] Skipped = ["logs", "backups"];

    /// <summary>
    /// Never in a backup and never replaced by one: the phones paired with this computer. Importing someone else's backup
    /// must not let their phones control this computer (and the tokens only open for this Windows account anyway).
    /// </summary>
    private static readonly string[] KeptOut = ["phones.json", "phones.json.tmp"];

    private static bool IsKeptOut(string relative) => KeptOut.Contains(relative.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    /// <summary>Earlier settings are kept here before an import replaces them.</summary>
    public static string SafetyDirectory(string dataDirectory) => Path.Combine(dataDirectory, "backups");

    /// <summary>Writes the backup of <paramref name="dataDirectory"/> to <paramref name="file"/>. Returns how many files it holds.</summary>
    public static int Create(string dataDirectory, string file, string appVersion)
    {
        string temp = file + ".tmp";
        int count = 0;
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (string path in Files(dataDirectory))
            {
                zip.CreateEntryFromFile(path, Relative(dataDirectory, path), CompressionLevel.Optimal);
                count++;
            }
            var manifest = new JsonObject
            {
                ["app"] = "SwitchMonitor",
                ["version"] = 1,
                ["appVersion"] = appVersion,
                ["created"] = DateTimeOffset.Now.ToString("O"),
                ["files"] = count
            };
            using var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open());
            writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        File.Move(temp, file, overwrite: true);
        return count;
    }

    /// <summary>
    /// Replaces the settings in <paramref name="dataDirectory"/> with those of <paramref name="file"/>. The current
    /// settings are saved first in backups\ (returned). Throws <see cref="FormatException"/> for a file that is not a
    /// SwitchMonitor backup; nothing is changed then.
    /// </summary>
    public static string Restore(string file, string dataDirectory, string appVersion)
    {
        using var zip = ZipFile.OpenRead(file);
        var manifest = zip.GetEntry(ManifestName) ?? throw new FormatException("This file is not a SwitchMonitor backup.");
        using (var reader = new StreamReader(manifest.Open()))
            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject root || (string?)root["app"] != "SwitchMonitor")
                throw new FormatException("This file is not a SwitchMonitor backup.");
        var entries = zip.Entries.Where(e => e.FullName != ManifestName && e.Length >= 0 && !e.FullName.EndsWith('/') && !IsKeptOut(e.FullName))
            .ToArray();
        string target = Path.GetFullPath(dataDirectory);
        foreach (var entry in entries)
        {
            string destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Skipped.Contains(entry.FullName.Split('/', '\\')[0], StringComparer.OrdinalIgnoreCase))
                throw new FormatException($"The backup holds a file outside the settings folder ({entry.FullName}).");
        }

        // The settings now, kept in case the import was a mistake.
        Directory.CreateDirectory(SafetyDirectory(dataDirectory));
        string safety = Path.Combine(SafetyDirectory(dataDirectory), $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        Create(dataDirectory, safety, appVersion);

        foreach (string path in Files(dataDirectory)) File.Delete(path);
        foreach (var entry in entries)
        {
            string destination = Path.Combine(target, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
        return safety;
    }

    private static IEnumerable<string> Files(string dataDirectory) =>
        !Directory.Exists(dataDirectory) ? [] :
        Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !Skipped.Contains(Relative(dataDirectory, path).Split('/')[0], StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsKeptOut(Relative(dataDirectory, path)))
            .ToArray();

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
