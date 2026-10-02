using System.Text.Json;
using SwitchMonitor.Wpf;

const string digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
const string url = "https://github.com/greypaulino/SwitchMonitor/releases/download/v9.9.9/";
const string manifest = "[{\"File\":\"SwitchMonitor-WPF-Setup-9.9.9.exe\",\"Hash\":\"" + digest + "\"}]";

async Task<AvailableUpdate?> Parse(string assets, string tag = "v9.9.9")
{
    using var release = JsonDocument.Parse("{\"tag_name\":\"" + tag + "\",\"assets\":[" + assets + "]}");
    return await UpdateService.ParseReleaseAsync(release.RootElement, default,
        (_, _) => Task.FromResult(manifest));
}

string installer = "{\"name\":\"SwitchMonitor-WPF-Setup-9.9.9.exe\",\"browser_download_url\":\"" + url + "SwitchMonitor-WPF-Setup-9.9.9.exe\"}";
string hash = "{\"name\":\"SHA256-WPF.json\",\"browser_download_url\":\"" + url + "SHA256-WPF.json\"}";
string ahk = "{\"name\":\"SwitchMonitor-Setup-9.9.9.exe\",\"browser_download_url\":\"" + url + "SwitchMonitor-Setup-9.9.9.exe\"}";

if ((await Parse(installer + "," + hash))?.Sha256 != digest ||
    await Parse(ahk + "," + hash) is not null ||
    await Parse(installer) is not null ||
    await Parse(installer + "," + hash, "v1.2") is not null ||
    UpdateService.IsReleaseAsset(new Uri("https://example.com/greypaulino/SwitchMonitor/releases/download/v9.9.9/update.exe")))
    throw new Exception("Release filtering failed.");

Console.WriteLine("WPF release filtering: PASS (no network access or install)");
