using System.Net;
using System.Security.Cryptography;
using System.Text;
using SwitchMonitor.Core.Updates;

namespace SwitchMonitor.Core.Tests;

/// <summary>The update engine without a network: a fake GitHub answers from memory.</summary>
internal static class UpdateTests
{
    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private const string Download = "https://github.com/greypaulino/SwitchMonitor/releases/download/v2.1.0/";

    private static string ReleaseJson(string tag = "v2.1.0", bool installer = true, bool manifest = true, bool signature = true, string host = "github.com") =>
        "{\"tag_name\":\"" + tag + "\",\"assets\":[" + string.Join(",", new[]
        {
            installer ? Asset("SwitchMonitor-Setup-2.1.0.exe", host) : null,
            manifest ? Asset("SHA256.json", host) : null,
            signature ? Asset("SHA256.json.sig", host) : null
        }.Where(a => a is not null)) + "]}";

    private static string Asset(string name, string host) =>
        "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://" + host + "/greypaulino/SwitchMonitor/releases/download/v2.1.0/" + name + "\"}";

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK, string? etag = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    [Test]
    public static void VersionsCompareNumberByNumber()
    {
        Assert.True(VersionNumber.TryParse("v2.0.0", out var v2) && v2.ToString() == "2.0.0");
        Assert.True(VersionNumber.TryParse("1.10.1.3", out var four) && four.ToString() == "1.10.1.3");
        Assert.True(VersionNumber.TryParse("2.1", out var two) && two.ToString() == "2.1.0");
        Assert.True(!VersionNumber.TryParse("2.x", out _) && !VersionNumber.TryParse("", out _) && !VersionNumber.TryParse("2", out _), "not a version");
        Assert.True(Parse("1.3.0") > Parse("1.2.0") && Parse("1.2.1") > Parse("1.2.0"), "newer");
        Assert.True(!(Parse("1.2.0") > Parse("1.2.0")) && !(Parse("1.1.9") > Parse("1.2.0")), "same or older");
        Assert.True(Parse("1.10.1") > Parse("1.9.9"), "10 is more than 9, not 1 then 0");
    }

    private static VersionNumber Parse(string text) => VersionNumber.TryParse(text, out var v) ? v : throw new FormatException(text);

    [Test]
    public static void TheManifestOfThe1xFormatIsRead()
    {
        // As Get-FileHash | ConvertTo-Json wrote it for 1.10.1: a BOM, two spaces after the colons, upper-case hashes.
        string json = "﻿[\n    {\n        \"File\":  \"SwitchMonitor-Setup-1.10.1.exe\",\n        \"Hash\":  \"3EB0A8CFBA3930D2DD65296BA82C31E48AB768B8D7A83AF76F3299E81B66DACB\"\n    },\n" +
            "    {\n        \"File\":  \"SwitchMonitor-Portable-1.10.1.zip\",\n        \"Hash\":  \"80aaa75fa5c745dde9ad924fde1b0d295544d8e994bbf5e4ede\"\n    }\n]";
        Assert.Equal("3EB0A8CFBA3930D2DD65296BA82C31E48AB768B8D7A83AF76F3299E81B66DACB", UpdateManifest.FindHash(json, "SwitchMonitor-Setup-1.10.1.exe"));
        Assert.Equal<string?>(null, UpdateManifest.FindHash(json, "SwitchMonitor-Portable-1.10.1.zip"), "a hash that is not 64 hex is refused");
        Assert.Equal<string?>(null, UpdateManifest.FindHash(json, "SwitchMonitor-Setup-9.9.9.exe"), "a file that is not listed");
        Assert.Equal<string?>(null, UpdateManifest.FindHash("not json", "x"), "garbage");
    }

    [Test]
    public static void OnlyTheAuthorsSignatureIsAccepted()
    {
        using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(author.ExportSubjectPublicKeyInfo());
        byte[] manifest = Encoding.UTF8.GetBytes("[{\"File\":\"a\",\"Hash\":\"b\"}]");
        string good = Convert.ToBase64String(author.SignData(manifest, HashAlgorithmName.SHA256));
        Assert.True(UpdateManifest.VerifySignature(manifest, good, publicKey), "signed by the author");
        Assert.True(!UpdateManifest.VerifySignature(Encoding.UTF8.GetBytes("changed"), good, publicKey), "manifest changed after signing");
        Assert.True(!UpdateManifest.VerifySignature(manifest, Convert.ToBase64String(other.SignData(manifest, HashAlgorithmName.SHA256)), publicKey), "signed by someone else");
        Assert.True(!UpdateManifest.VerifySignature(manifest, "not base64!", publicKey), "garbage signature");
    }

    [Test]
    public static void TheFeedFindsANewerReleaseAndItsFiles()
    {
        var handler = new FakeGitHub(_ => Json(ReleaseJson(), etag: "\"abc\""));
        var feed = new ReleaseFeed(new HttpClient(handler));
        var check = feed.CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult();
        Assert.Equal(UpdateCheckStatus.Available, check.Status);
        Assert.Equal("2.1.0", check.Update!.Version.ToString());
        Assert.Equal("SwitchMonitor-Setup-2.1.0.exe", check.Update.InstallerName);
        Assert.True(check.Update.SignatureUrl is not null);
        Assert.Equal("\"abc\"", check.ETag);
        Assert.True(handler.Requests[0].Headers.UserAgent.ToString().StartsWith("SwitchMonitor/"), "GitHub needs a user agent");
        Assert.Equal(UpdateCheckStatus.UpToDate, feed.CheckAsync(Parse("2.1.0")).GetAwaiter().GetResult().Status, "same version");
        Assert.Equal(UpdateCheckStatus.UpToDate, feed.CheckAsync(Parse("3.0.0")).GetAwaiter().GetResult().Status, "older release");
    }

    [Test]
    public static void TheFeedHandlesNothingNewMissingFilesAndStrangeAddresses()
    {
        HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> r) => new(new FakeGitHub(r));
        Assert.Equal(UpdateCheckStatus.NoReleases, new ReleaseFeed(Client(_ => Json("{}", HttpStatusCode.NotFound))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult().Status, "404 = no releases yet");
        var cached = new ReleaseFeed(Client(r => new HttpResponseMessage(r.Headers.Contains("If-None-Match") ? HttpStatusCode.NotModified : HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) }));
        Assert.Equal(UpdateCheckStatus.NotModified, cached.CheckAsync(Parse("2.0.0"), "\"abc\"").GetAwaiter().GetResult().Status, "ETag: GitHub says nothing changed");
        var noInstaller = new ReleaseFeed(Client(_ => Json(ReleaseJson(installer: false)))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult();
        Assert.Equal(UpdateCheckStatus.Failed, noInstaller.Status);
        Assert.True(noInstaller.Error!.Contains("missing its installer"), "the 1.x wording");
        var foreign = new ReleaseFeed(Client(_ => Json(ReleaseJson(host: "evil.example")))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult();
        Assert.Equal(UpdateCheckStatus.Failed, foreign.Status, "files hosted elsewhere are never offered");
        Assert.Equal(UpdateCheckStatus.Failed, new ReleaseFeed(Client(_ => Json("oops", HttpStatusCode.InternalServerError))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult().Status);
        Assert.Equal(UpdateCheckStatus.Failed, new ReleaseFeed(Client(_ => Json("{\"tag_name\":\"nightly\"}"))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult().Status, "a tag that is not a version");
        Assert.Equal(UpdateCheckStatus.Failed, new ReleaseFeed(Client(_ => throw new HttpRequestException("offline"))).CheckAsync(Parse("2.0.0")).GetAwaiter().GetResult().Status, "no network");
    }

    // ---- The download: nothing is kept unless it is the published installer ----

    private sealed class Release
    {
        public required byte[] Installer { get; init; }
        public required string Manifest { get; init; }
        public required string Signature { get; init; }
        public required string PublicKey { get; init; }
    }

    private static Release MakeRelease(byte[]? installer = null, Func<string, string>? manifestOf = null, bool sign = true)
    {
        installer ??= [.. new byte[] { (byte)'M', (byte)'Z' }, .. new byte[200_000]];
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string hash = Convert.ToHexString(SHA256.HashData(installer));
        string manifest = "[{\"File\":\"SwitchMonitor-Setup-2.1.0.exe\",\"Hash\":\"" + (manifestOf?.Invoke(hash) ?? hash) + "\"}]";
        string signature = sign ? Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256))
            : Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));
        return new Release { Installer = installer, Manifest = manifest, Signature = signature, PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
    }

    private static UpdateInfo Info() => new(Parse("2.1.0"), "v2.1.0", "SwitchMonitor-Setup-2.1.0.exe",
        new Uri(Download + "SwitchMonitor-Setup-2.1.0.exe"), new Uri(Download + "SHA256.json"), new Uri(Download + "SHA256.json.sig"));

    private static (DownloadedUpdate? Result, string? Error, string Folder) Run(Release release, bool requireSignature = true, Uri? installerUrl = null)
    {
        var handler = new FakeGitHub(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("SHA256.json") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release.Manifest) },
            var p when p.EndsWith("SHA256.json.sig") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release.Signature) },
            var p when p.EndsWith(".exe") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Installer) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var http = new HttpClient(handler);
        var downloader = new UpdateDownloader(http, new ReleaseFeed(http), requireSignature, release.PublicKey);
        string folder = Path.Combine(Path.GetTempPath(), "sm-update-test-" + Guid.NewGuid().ToString("N"));
        var info = installerUrl is null ? Info() : Info() with { InstallerUrl = installerUrl };
        try { return (downloader.DownloadAsync(info, folder).GetAwaiter().GetResult(), null, folder); }
        catch (InvalidDataException error) { return (null, error.Message, folder); }
    }

    [Test]
    public static void AGoodInstallerIsKeptAndEverythingElseIsRefused()
    {
        var good = Run(MakeRelease());
        Assert.True(good.Result is not null && File.Exists(good.Result.InstallerPath), "published, signed, right hash: kept");
        Assert.True(!Directory.GetFiles(good.Folder, "*.part").Any(), "no half-downloaded file is left");

        var wrongHash = Run(MakeRelease(manifestOf: _ => new string('A', 64)));
        Assert.True(wrongHash.Error!.Contains("checksum does not match"), "hash differs from the manifest");
        Assert.True(!Directory.GetFiles(wrongHash.Folder).Any(), "and nothing stays on disk");

        var unsigned = Run(MakeRelease(sign: false));
        Assert.True(unsigned.Error!.Contains("not signed by the author"), "manifest signed by someone else");
        Assert.True(Run(MakeRelease(sign: false), requireSignature: false).Result is not null, "signature checking can be turned off (tests, portable builds)");

        var tiny = Run(MakeRelease(installer: [(byte)'M', (byte)'Z', 0, 0]));
        Assert.True(tiny.Error!.Contains("too small"), "a few bytes cannot be an installer");
        var notExe = Run(MakeRelease(installer: new byte[200_000]));
        Assert.True(notExe.Error!.Contains("not an installer"), "no MZ header");

        var outside = Run(MakeRelease(), installerUrl: new Uri("https://evil.example/SwitchMonitor-Setup-2.1.0.exe"));
        Assert.True(outside.Error!.Contains("outside"), "an address outside the project's releases");
    }
}
