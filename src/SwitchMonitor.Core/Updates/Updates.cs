using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Core.Updates;

/// <summary>A version of up to four numbers ("2.0.0", "v1.10.1.3"), compared number by number like the 1.x engine did.</summary>
public readonly record struct VersionNumber(int Major, int Minor, int Patch, int Build) : IComparable<VersionNumber>
{
    public static bool TryParse(string? text, out VersionNumber version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Regex.Match(text.Trim(), @"^v?(\d+(?:\.\d+){1,3})$");
        if (!match.Success) return false;
        var parts = match.Groups[1].Value.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1).ToArray();
        if (parts.Any(p => p < 0)) return false;
        version = new VersionNumber(parts[0], parts[1], parts.Length > 2 ? parts[2] : 0, parts.Length > 3 ? parts[3] : 0);
        return true;
    }

    public int CompareTo(VersionNumber other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        return c != 0 ? c : Build.CompareTo(other.Build);
    }

    public static bool operator >(VersionNumber a, VersionNumber b) => a.CompareTo(b) > 0;
    public static bool operator <(VersionNumber a, VersionNumber b) => a.CompareTo(b) < 0;

    /// <summary>"2.0.0"; a fourth number only when it is not zero.</summary>
    public override string ToString() => Build == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{Build}";
}

/// <summary>The public key that checks the signature of SHA256.json (the private half never leaves the author's computer).</summary>
public static class UpdateKeys
{
    /// <summary>ECDSA P-256, SubjectPublicKeyInfo, base64.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERa8OaKbbfmP8Zo+/sYK98Qp5mHiZj7Xb08ZNLlzn0/gS1fNyhEA4yYoFkHlCl1363/zvAMdtsvJG0O9jbJTgDw==";
}

/// <summary>SHA256.json: the list of release files and their hashes (the 1.x format), and its signature.</summary>
public static class UpdateManifest
{
    /// <summary>The 64-hex hash (upper case) of <paramref name="fileName"/> in the manifest, or null. Tolerates a BOM and any spacing.</summary>
    public static string? FindHash(string json, string fileName)
    {
        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('﻿'));
            var entries = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray() : default;
            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                string? file = entry.TryGetProperty("File", out var f) ? f.GetString() : null;
                string? hash = entry.TryGetProperty("Hash", out var h) ? h.GetString() : null;
                if (string.Equals(file, fileName, StringComparison.OrdinalIgnoreCase) && hash is not null && Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$"))
                    return hash.ToUpperInvariant();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>True when <paramref name="signatureBase64"/> (raw r||s, base64) is a valid ECDSA P-256 signature of <paramref name="manifest"/>.</summary>
    public static bool VerifySignature(byte[] manifest, string signatureBase64, string publicKeyBase64 = UpdateKeys.PublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(manifest, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is FormatException or CryptographicException) { return false; }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>What the latest release offers: its version and the three files the engine needs.</summary>
public sealed record UpdateInfo(VersionNumber Version, string Tag, string InstallerName, Uri InstallerUrl, Uri ManifestUrl, Uri? SignatureUrl);

public enum UpdateCheckStatus { UpToDate, Available, NoReleases, NotModified, Failed }

public sealed record UpdateCheck(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null, string? ETag = null);

/// <summary>Asks GitHub for the latest release (the same source and file names as the 1.x engine).</summary>
public sealed class ReleaseFeed(HttpClient http, string repository = ReleaseFeed.DefaultRepository)
{
    public const string DefaultRepository = "greypaulino/SwitchMonitor";
    public const string ManifestName = "SHA256.json";
    public const string SignatureName = "SHA256.json.sig";

    public static string InstallerFileName(VersionNumber version) => $"SwitchMonitor-Setup-{version}.exe";

    /// <summary>Only files published by this repository's releases are ever downloaded.</summary>
    public bool IsAllowed(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.StartsWith($"/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public async Task<UpdateCheck> CheckAsync(VersionNumber current, string? etag = null, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"SwitchMonitor/{current}");
            if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified) return new UpdateCheck(UpdateCheckStatus.NotModified, ETag: etag);
            if (response.StatusCode == HttpStatusCode.NotFound) return new UpdateCheck(UpdateCheckStatus.NoReleases);
            if (!response.IsSuccessStatusCode) return new UpdateCheck(UpdateCheckStatus.Failed, Error: $"GitHub answered HTTP {(int)response.StatusCode}.");
            string newTag = response.Headers.ETag?.Tag ?? "";
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
            var root = document.RootElement;
            if (!VersionNumber.TryParse(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null, out var latest))
                return new UpdateCheck(UpdateCheckStatus.Failed, Error: "The latest release has no supported version tag.", ETag: newTag);
            if (!(latest > current)) return new UpdateCheck(UpdateCheckStatus.UpToDate, ETag: newTag);

            string installer = InstallerFileName(latest);
            Uri? installerUrl = null, manifestUrl = null, signatureUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!Uri.TryCreate(asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null, UriKind.Absolute, out var url) || !IsAllowed(url)) continue;
                    if (string.Equals(name, installer, StringComparison.OrdinalIgnoreCase)) installerUrl = url;
                    else if (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase)) manifestUrl = url;
                    else if (string.Equals(name, SignatureName, StringComparison.OrdinalIgnoreCase)) signatureUrl = url;
                }
            if (installerUrl is null || manifestUrl is null)
                return new UpdateCheck(UpdateCheckStatus.Failed, Error: "The release is missing its installer or SHA256 manifest.", ETag: newTag);
            return new UpdateCheck(UpdateCheckStatus.Available, new UpdateInfo(latest, root.GetProperty("tag_name").GetString()!, installer, installerUrl, manifestUrl, signatureUrl), ETag: newTag);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new UpdateCheck(UpdateCheckStatus.Failed, Error: $"Could not check for updates: {error.Message}");
        }
    }
}

public sealed record DownloadedUpdate(string InstallerPath, string Hash);

/// <summary>Downloads the installer next to the app's data and proves it is the published one before anything runs it.</summary>
public sealed class UpdateDownloader(HttpClient http, ReleaseFeed feed, bool requireSignature = true, string publicKey = UpdateKeys.PublicKey)
{
    public const long MinimumInstallerSize = 100_000;

    /// <exception cref="InvalidDataException">The file is not what the release published (hash, signature, size or format).</exception>
    public async Task<DownloadedUpdate> DownloadAsync(UpdateInfo update, string folder, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (!feed.IsAllowed(update.InstallerUrl) || !feed.IsAllowed(update.ManifestUrl) || (update.SignatureUrl is { } s && !feed.IsAllowed(s)))
            throw new InvalidDataException("The release points outside this project's GitHub releases.");
        Directory.CreateDirectory(folder);

        byte[] manifest = await GetBytesAsync(update.ManifestUrl, cancel).ConfigureAwait(false);
        if (requireSignature)
        {
            if (update.SignatureUrl is null) throw new InvalidDataException("The release has no signature for its SHA256 manifest.");
            string signature = Encoding.ASCII.GetString(await GetBytesAsync(update.SignatureUrl, cancel).ConfigureAwait(false));
            if (!UpdateManifest.VerifySignature(manifest, signature, publicKey))
                throw new InvalidDataException("The SHA256 manifest is not signed by the author of SwitchMonitor.");
        }
        string expected = UpdateManifest.FindHash(Encoding.UTF8.GetString(manifest), update.InstallerName)
            ?? throw new InvalidDataException("The release has no valid installer checksum.");

        string target = Path.Combine(folder, update.InstallerName);
        string partial = $"{target}.{Environment.ProcessId}.part";
        try
        {
            using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var file = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }
            }
            var info = new FileInfo(partial);
            if (info.Length < MinimumInstallerSize) throw new InvalidDataException("The downloaded file is too small to be the installer.");
            using (var head = File.OpenRead(partial))
                if (head.ReadByte() != 'M' || head.ReadByte() != 'Z') throw new InvalidDataException("The downloaded file is not an installer.");
            string actual = UpdateManifest.Sha256(partial);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer checksum does not match the published release.");
            File.Move(partial, target, overwrite: true);
            return new DownloadedUpdate(target, actual);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (IOException) { }
        }
    }

    private async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    }
}

/// <summary>Which edition of the app is running: only the installed one updates itself.</summary>
public enum UpdateEdition { Installed, Portable, Packaged }

public static class UpdateEditions
{
    private const int NoPackage = 15700;   // APPMODEL_ERROR_NO_PACKAGE

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    /// <summary>MSIX / Store (updated by Windows), the installed edition (has installed.flag beside the exe), or portable.</summary>
    public static UpdateEdition Detect(string? exeFolder = null)
    {
        int length = 0;
        try { if (GetCurrentPackageFullName(ref length, null) != NoPackage) return UpdateEdition.Packaged; }
        catch (EntryPointNotFoundException) { }
        exeFolder ??= AppContext.BaseDirectory;
        return File.Exists(Path.Combine(exeFolder, "installed.flag")) ? UpdateEdition.Installed : UpdateEdition.Portable;
    }
}

/// <summary>Remembers what the update engine needs between runs (updates.json in the data folder).</summary>
public sealed class UpdateStore(string? path = null)
{
    private readonly string file = path ?? Path.Combine(AppPaths.DataDirectory, "updates.json");

    public bool AutoCheck { get; set; } = true;
    public DateTimeOffset? LastCheck { get; set; }
    public string? ETag { get; set; }
    public string? NotifiedVersion { get; set; }

    public static UpdateStore Load(string? path = null)
    {
        var store = new UpdateStore(path);
        try
        {
            if (!File.Exists(store.file)) return store;
            using var document = JsonDocument.Parse(File.ReadAllText(store.file));
            var root = document.RootElement;
            if (root.TryGetProperty("autoCheck", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False) store.AutoCheck = a.GetBoolean();
            if (root.TryGetProperty("lastCheck", out var l) && DateTimeOffset.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) store.LastCheck = when;
            store.ETag = root.TryGetProperty("etag", out var e) ? e.GetString() : null;
            store.NotifiedVersion = root.TryGetProperty("notified", out var n) ? n.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                autoCheck = AutoCheck, lastCheck = LastCheck?.ToString("o", CultureInfo.InvariantCulture), etag = ETag, notified = NotifiedVersion
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
