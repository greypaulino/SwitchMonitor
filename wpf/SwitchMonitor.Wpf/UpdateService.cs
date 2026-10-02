using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Wpf;

internal sealed record AvailableUpdate(Version Version, Uri InstallerUrl, string Sha256);

internal static class UpdateService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            UserAgent = { new System.Net.Http.Headers.ProductInfoHeaderValue("SwitchMonitor-WPF", "0.1") },
            Accept = { new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json") }
        }
    };
    private const string ReleaseApi = "https://api.github.com/repos/greypaulino/SwitchMonitor/releases/latest";

    internal static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ReleaseApi, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return await ParseReleaseAsync(document.RootElement, cancellationToken);
    }

    internal static async Task<AvailableUpdate?> ParseReleaseAsync(JsonElement release,
        CancellationToken cancellationToken = default, Func<Uri, CancellationToken, Task<string>>? readManifest = null)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^[vV]?\d+\.\d+\.\d+$") ||
            !Version.TryParse(tag.TrimStart('v', 'V'), out var version) ||
            version <= typeof(UpdateService).Assembly.GetName().Version)
            return null;
        string fileName = $"SwitchMonitor-WPF-Setup-{version.ToString(3)}.exe";
        Uri? installer = null;
        Uri? manifest = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsReleaseAsset(uri)) continue;
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) installer = uri;
            if (name.Equals("SHA256-WPF.json", StringComparison.OrdinalIgnoreCase)) manifest = uri;
        }
        if (installer is null || manifest is null) return null;
        string json = readManifest is null
            ? await Client.GetStringAsync(manifest, cancellationToken)
            : await readManifest(manifest, cancellationToken);
        using var hashes = JsonDocument.Parse(json);
        foreach (var entry in hashes.RootElement.EnumerateArray())
        {
            if (!string.Equals(entry.GetProperty("File").GetString(), fileName,
                    StringComparison.OrdinalIgnoreCase)) continue;
            string hash = entry.GetProperty("Hash").GetString() ?? "";
            if (Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$"))
                return new AvailableUpdate(version, installer, hash.ToUpperInvariant());
        }
        return null;
    }

    internal static bool IsReleaseAsset(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps &&
        url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.StartsWith("/greypaulino/SwitchMonitor/releases/download/",
            StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> DownloadVerifiedAsync(AvailableUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (!IsReleaseAsset(update.InstallerUrl))
            throw new InvalidOperationException("The update URL is not a GitHub release asset for SwitchMonitor.");
        string directory = Path.Combine(Path.GetDirectoryName(Preferences.SettingsPath)!, "updates");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, $"SwitchMonitor-WPF-Setup-{update.Version.ToString(3)}.exe");
        string partial = target + ".part";
        try
        {
            using var response = await Client.GetAsync(update.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(partial))
                await source.CopyToAsync(destination, cancellationToken);
            string actual;
            await using (var file = File.OpenRead(partial))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer does not match its published SHA-256 hash.");
            File.Move(partial, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}
