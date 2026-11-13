using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Scenes;

/// <summary>
/// Profiles shared between the computers of a group. Each profile carries the time it was last changed ("modified", in scenes.json) and a
/// deleted one leaves a note ("removed"): when two computers compare, the newer change wins, one profile at a time. A profile tied to a
/// display travels with that display's EDID (name and serial), which is the same on every computer, and is tied again to the matching
/// display here when there is one. Shortcuts stay personal: they are never sent and never replaced.
/// </summary>
public static class SceneSync
{
    /// <summary>Source of "now" in milliseconds (replaced by tests).</summary>
    public static Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A picture bigger than this is not sent (the profile arrives with its usual icon).</summary>
    public const int MaxPictureBytes = 200_000;

    // ---- Marking changes (called by the editing functions of SceneStore) ----

    internal static void Touch(JsonNode root, JsonObject scene)
    {
        long before = scene["modified"] is JsonValue v && v.TryGetValue<long>(out long old) ? old : 0;
        scene["modified"] = Math.Max(Clock(), before + 1);
        if (root["removed"] is JsonObject removed && scene["name"]?.GetValue<string>() is { } name) removed.Remove(name.Trim().ToLowerInvariant());
    }

    internal static void Forget(JsonNode root, string name)
    {
        if (root["removed"] is not JsonObject removed) root["removed"] = removed = new JsonObject();
        removed[name.Trim().ToLowerInvariant()] = Clock();
    }

    /// <summary>Stable text for a display that is the same on every computer ("odyssey g5|h4zr600017"), or null when its EDID is unknown.</summary>
    public static string? EdidKey(MonitorIdentity identity) =>
        identity.Edid is { Name: { Length: > 0 } name } edid ? $"{name}|{edid.Serial}".ToLowerInvariant() : null;

    // ---- What is sent ----

    /// <summary>
    /// This computer's profiles for another one: without shortcuts, with display keys turned into EDID keys and with pictures inside.
    /// </summary>
    /// <param name="edidOf">The EDID key of the display on this computer with that key (null when unknown).</param>
    public static JsonObject Snapshot(string json, string iconsDirectory, Func<string, string?> edidOf)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new FormatException("scenes.json is empty.");
        var scenes = new JsonArray();
        int budget = 600_000;
        foreach (var source in (root["scenes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var scene = (JsonObject)source.DeepClone();
            scene.Remove("hotkey");
            if ((string?)scene["monitorKey"] is { } key)
            {
                if (edidOf(key) is { } edid) scene["edidKey"] = edid;
                scene.Remove("monitorKey");
            }
            if ((string?)scene["icon"] is { } icon && icon.StartsWith("image:", StringComparison.Ordinal))
            {
                string file = Path.Combine(iconsDirectory, icon[6..]);
                if (SceneStore.NormalizeIcon(icon) is not null && File.Exists(file) && new FileInfo(file).Length <= MaxPictureBytes
                    && budget > 0)
                {
                    string data = Convert.ToBase64String(File.ReadAllBytes(file));
                    budget -= data.Length;
                    scene["imageData"] = data;
                }
                else scene.Remove("icon");
            }
            scenes.Add(scene);
        }
        var removed = root["removed"] is JsonObject r ? (JsonObject)r.DeepClone() : new JsonObject();
        return new JsonObject { ["scenes"] = scenes, ["removed"] = removed };
    }

    // ---- What is received ----

    /// <summary>
    /// Joins another computer's profiles into <paramref name="json"/>: a profile only the other has is added, one both have takes the
    /// newer version, one deleted over there after it was last changed here is deleted here. Returns the new text and whether anything
    /// changed; a profile this computer cannot read (an unknown control, a program already linked to another profile) is skipped.
    /// </summary>
    /// <param name="keyOfEdid">The key of the display on this computer with that EDID key (null when there is none).</param>
    public static (string Json, bool Changed) Merge(string json, JsonObject theirs, string iconsDirectory, Func<string, string?> keyOfEdid)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                   ?? throw new FormatException("scenes.json is empty.");
        var scenes = root["scenes"] as JsonArray ?? throw new FormatException("scenes.json must contain a \"scenes\" array.");
        string current = root.ToJsonString();
        bool changed = false;

        JsonObject? Find(string name) => scenes.OfType<JsonObject>()
            .FirstOrDefault(s => string.Equals(s["name"]?.GetValue<string>()?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        static long Modified(JsonObject? s) => s?["modified"] is JsonValue v && v.TryGetValue<long>(out long n) ? n : 0;
        long Deleted(string name) => root["removed"] is JsonObject r && r[name.Trim().ToLowerInvariant()] is JsonValue v && v.TryGetValue<long>(out long n) ? n : 0;

        // Deletions first.
        if (theirs["removed"] is JsonObject theirRemoved)
            foreach (var (name, node) in theirRemoved)
            {
                if (node is not JsonValue v || !v.TryGetValue<long>(out long at) || name.Length is 0 or > 80) continue;
                if (Find(name) is { } mine && Modified(mine) <= at)
                {
                    scenes.Remove(mine);
                    changed = true;
                }
                if (Deleted(name) < at && Find(name) is null)
                {
                    if (root["removed"] is not JsonObject own) root["removed"] = own = new JsonObject();
                    own[name.Trim().ToLowerInvariant()] = at;
                    changed = true;
                }
            }

        if (theirs["scenes"] is JsonArray theirScenes)
            foreach (var incoming in theirScenes.OfType<JsonObject>().Take(200))
            {
                if (incoming["name"]?.GetValue<string>()?.Trim() is not { Length: > 0 and <= 80 } name
                    || string.Equals(name, SceneStore.CustomName, StringComparison.OrdinalIgnoreCase)) continue;
                long at = Modified(incoming);
                var mine = Find(name);
                if (mine is null ? Deleted(name) > 0 && Deleted(name) >= at : at <= Modified(mine)) continue;

                var scene = (JsonObject)incoming.DeepClone();
                scene.Remove("hotkey");
                scene.Remove("monitorKey");
                if ((string?)scene["edidKey"] is { } edid && keyOfEdid(edid) is { } localKey) scene["monitorKey"] = localKey;
                scene.Remove("edidKey");
                if (mine?["hotkey"] is { } hotkey) scene["hotkey"] = hotkey.DeepClone();
                string? picture = null;
                if ((string?)scene["imageData"] is { } data)
                {
                    try { picture = SavePicture(scene, data, iconsDirectory); }
                    catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException) { picture = null; }
                }
                scene.Remove("imageData");
                if ((string?)scene["icon"] is { } icon && icon.StartsWith("image:", StringComparison.Ordinal)) { if (picture is null) scene.Remove("icon"); else scene["icon"] = picture; }

                // Try it: when the result cannot be read back, this profile is left out and the rest still joins.
                var trial = JsonNode.Parse(root.ToJsonString())!;
                var trialScenes = (JsonArray)trial["scenes"]!;
                if (trialScenes.OfType<JsonObject>().FirstOrDefault(s => string.Equals(s["name"]?.GetValue<string>()?.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } old)
                    trialScenes.Remove(old);
                trialScenes.Add(scene.DeepClone());
                if (trial["removed"] is JsonObject trialRemoved) trialRemoved.Remove(name.ToLowerInvariant());
                try { SceneStore.Parse(trial.ToJsonString()); }
                catch (Exception error) when (error is FormatException or JsonException) { continue; }

                if (mine is not null) scenes.Remove(mine);
                scenes.Add(scene);
                if (root["removed"] is JsonObject removed) removed.Remove(name.ToLowerInvariant());
                changed = true;
            }

        string result = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return changed && result != current ? (result, true) : (json, false);
    }

    /// <summary>Saves the picture of a received profile in the icons folder (a different picture under the same name gets its own file).</summary>
    private static string? SavePicture(JsonObject scene, string base64, string iconsDirectory)
    {
        if ((string?)scene["icon"] is not { } icon || SceneStore.NormalizeIcon(icon) is not { } valid || !valid.StartsWith("image:", StringComparison.Ordinal)) return null;
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length is 0 or > MaxPictureBytes) return null;
        Directory.CreateDirectory(iconsDirectory);
        string file = valid[6..], path = Path.Combine(iconsDirectory, file);
        if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            file = Path.GetFileNameWithoutExtension(file) + "-" + Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant() + ".png";
            path = Path.Combine(iconsDirectory, file);
        }
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return "image:" + file;
    }
}
