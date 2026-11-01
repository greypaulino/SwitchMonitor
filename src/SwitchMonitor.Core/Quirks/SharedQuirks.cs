using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Quirks;

/// <summary>
/// The shared monitor database: the project publishes quirks.json and its SHA-256 next to it; the app downloads both,
/// checks the hash and that the file reads, then keeps it as quirks.shared.json. Learned settings travel as
/// .smmonitor files (one model's learned controls) that anyone can import or send to the project.
/// </summary>
public static class SharedQuirks
{
    /// <summary>Where the project publishes the database (the v2 branch until v2 becomes the main branch).</summary>
    public const string DatabaseUrl = "https://raw.githubusercontent.com/greypaulino/SwitchMonitor/v2-rewrite/database/quirks.json";
    public const string HashUrl = DatabaseUrl + ".sha256";
    public const string MonitorExtension = ".smmonitor";

    /// <summary>Lowercase hex SHA-256 of <paramref name="content"/>.</summary>
    public static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Checks a download: the hash file ("&lt;hex&gt;" or "&lt;hex&gt;  quirks.json") must match and the database must read.
    /// Returns how many monitor models it describes; throws <see cref="FormatException"/> otherwise.
    /// </summary>
    public static int Verify(byte[] content, string hashText)
    {
        string expected = hashText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new FormatException("The published SHA-256 is not valid.");
        if (Hash(content) != expected) throw new FormatException("The download does not match its SHA-256: it was not installed.");
        return QuirkDatabase.Parse(System.Text.Encoding.UTF8.GetString(content).TrimStart('\uFEFF')).All.Count;
    }

    /// <summary>Checks and installs a verified download as the shared database.</summary>
    public static int Install(byte[] content, string hashText, string? path = null)
    {
        int count = Verify(content, hashText);
        path ??= QuirkDatabase.SharedFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
        return count;
    }

    /// <summary>
    /// The learned controls of one model (from quirks.user.json) as a file to share, or null when nothing was learned
    /// for it.
    /// </summary>
    public static string? ExportLearned(string quirkId, string? userFile = null)
    {
        userFile ??= QuirkDatabase.UserFilePath;
        if (!File.Exists(userFile)) return null;
        var root = JsonNode.Parse(File.ReadAllText(userFile)) as JsonObject;
        var entry = (root?["monitors"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(m => string.Equals((string?)m["id"], quirkId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || (entry["controls"] as JsonArray)?.Count is not > 0) return null;
        var file = new JsonObject
        {
            ["app"] = "SwitchMonitor",
            ["kind"] = "monitor",
            ["version"] = 1,
            ["monitors"] = new JsonArray(entry.DeepClone())
        };
        return file.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Adds the learned controls of a shared file to this computer's quirks.user.json (same code replaces). Checked
    /// before anything is written. Returns how many controls were added.
    /// </summary>
    public static int ImportLearned(string fileJson, string? userFile = null)
    {
        if (JsonNode.Parse(fileJson) is not JsonObject root || (string?)root["app"] != "SwitchMonitor" || (string?)root["kind"] != "monitor")
            throw new FormatException("This file is not a SwitchMonitor monitor file.");
        QuirkDatabase.Parse(fileJson);   // reads like quirks.json: refused before it is merged otherwise
        return Learning.LearnedSettingsMerge.MergeInto(fileJson, userFile ?? QuirkDatabase.UserFilePath);
    }
}
