using System.Net;
using System.Net.Http;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneClickDpi.App;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: UpdateSmoke <manifest.json> <manifest.sig> <package.ocdupdate> <package.zip>");
    return 64;
}

var manifestBytes = await File.ReadAllBytesAsync(args[0]);
var signatureText = await File.ReadAllTextAsync(args[1]);
var signatureBytes = Convert.FromBase64String(signatureText.Trim());
var encryptedPackageBytes = await File.ReadAllBytesAsync(args[2]);
var packageBytes = await File.ReadAllBytesAsync(args[3]);
var manifest = UpdateSecurity.VerifyAndParseManifest(manifestBytes, signatureBytes);
Console.WriteLine("PASS valid ECDSA manifest signature");

var tamperedManifest = manifestBytes.ToArray();
tamperedManifest[^1] ^= 1;
try
{
    UpdateSecurity.VerifyAndParseManifest(tamperedManifest, signatureBytes);
    throw new InvalidOperationException("Tampered manifest was accepted.");
}
catch (CryptographicException)
{
    Console.WriteLine("PASS tampered manifest rejected");
}

var tag = "v" + manifest.Version;
var releaseBase = $"https://github.com/{GitHubUpdateClient.Repository}/releases/download/{tag}/";
var assetApiBase = $"https://api.github.com/repos/{GitHubUpdateClient.Repository}/releases/assets/";
var apiUri = new Uri("https://api.github.com/test/releases/latest");
var apiJson = JsonSerializer.SerializeToUtf8Bytes(new
{
    tag_name = tag,
    html_url = $"https://github.com/{GitHubUpdateClient.Repository}/releases/tag/{tag}",
    draft = false,
    prerelease = false,
    assets = new object[]
    {
        Asset(1, "OneClickDpi-update.json", manifestBytes.Length, releaseBase, assetApiBase, null),
        Asset(2, "OneClickDpi-update.sig", Encoding.ASCII.GetByteCount(signatureText), releaseBase, assetApiBase, null),
        Asset(3, manifest.DownloadFileName, encryptedPackageBytes.LongLength, releaseBase, assetApiBase, "sha256:" + manifest.DownloadSha256)
    }
});

var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal)
{
    [assetApiBase + "1"] = manifestBytes,
    [assetApiBase + "2"] = Encoding.ASCII.GetBytes(signatureText),
    [assetApiBase + "3"] = encryptedPackageBytes
};
var temporary = Path.Combine(Path.GetTempPath(), "OneClickDpi.UpdateSmoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var runtime = RuntimeAssetExtractor.EnsureExtracted(Path.Combine(temporary, "LocalData"));
    var requiredRuntimeFiles = new[]
    {
        Path.Combine(runtime.EngineDirectory, "winws.exe"),
        Path.Combine(runtime.EngineDirectory, "WinDivert.dll"),
        Path.Combine(runtime.EngineDirectory, "WinDivert64.sys"),
        Path.Combine(runtime.TorDirectory, "tor.exe")
    };
    if (requiredRuntimeFiles.Any(file => !File.Exists(file)))
    {
        throw new InvalidOperationException("Embedded runtime extraction is incomplete.");
    }

    File.Delete(Path.Combine(runtime.EngineDirectory, "winws.exe"));
    RuntimeAssetExtractor.EnsureExtracted(Path.Combine(temporary, "LocalData"));
    if (!File.Exists(Path.Combine(runtime.EngineDirectory, "winws.exe")))
    {
        throw new InvalidOperationException("A missing embedded runtime file was not restored.");
    }

    Console.WriteLine("PASS embedded runtime extracted and repaired");

    using (var client = new GitHubUpdateClient(
        temporary,
        apiUri,
        new FakeHandler(apiUri, apiJson, assets)))
    {
        var release = await client.CheckAsync(new Version(0, 0, 0), CancellationToken.None)
            ?? throw new InvalidOperationException("A newer signed release was not detected.");
        var prepared = await client.DownloadAsync(release, null, CancellationToken.None);
        await UpdateSecurity.VerifyPackageAsync(prepared.PackagePath, release.Manifest, CancellationToken.None);
        Console.WriteLine("PASS signed release discovery and verified download");

        var staging = Path.Combine(temporary, "staging");
        Directory.CreateDirectory(staging);
        var extracted = UpdateInstaller.ExtractVerifiedPackage(prepared.PackagePath, release.Manifest, staging);
        if (!extracted.Contains("OneClickDpi.exe", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The verified package did not extract the application executable.");
        }

        Console.WriteLine("PASS verified package extraction");

        var currentResult = await client.CheckAsync(release.Version, CancellationToken.None);
        if (currentResult is not null)
        {
            throw new InvalidOperationException("The current version was incorrectly offered as an update.");
        }

        Console.WriteLine("PASS current release is not reinstalled");
    }

    var damagedAssets = new Dictionary<string, byte[]>(assets, StringComparer.Ordinal);
    var damagedPackage = encryptedPackageBytes.ToArray();
    damagedPackage[damagedPackage.Length / 2] ^= 1;
    damagedAssets[assetApiBase + "3"] = damagedPackage;
    using var damagedClient = new GitHubUpdateClient(
        Path.Combine(temporary, "damaged"),
        apiUri,
        new FakeHandler(apiUri, apiJson, damagedAssets));
    var damagedRelease = await damagedClient.CheckAsync(new Version(0, 0, 0), CancellationToken.None)
        ?? throw new InvalidOperationException("Damaged test release was not detected.");
    try
    {
        await damagedClient.DownloadAsync(damagedRelease, null, CancellationToken.None);
        throw new InvalidOperationException("Damaged package was accepted.");
    }
    catch (CryptographicException)
    {
        Console.WriteLine("PASS damaged package rejected");
    }

    var unsafeArchive = Path.Combine(temporary, "unsafe.zip");
    using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry($"OneClickDpi-MVP-{manifest.Version}-win-x64/../escape.txt");
        await using var stream = entry.Open();
        await stream.WriteAsync("unsafe"u8.ToArray());
    }

    var unsafeStaging = Path.Combine(temporary, "unsafe-staging");
    Directory.CreateDirectory(unsafeStaging);
    try
    {
        UpdateInstaller.ExtractVerifiedPackage(unsafeArchive, manifest, unsafeStaging);
        throw new InvalidOperationException("Unsafe archive path was accepted.");
    }
    catch (InvalidDataException)
    {
        Console.WriteLine("PASS archive path traversal rejected");
    }
}
finally
{
    Directory.Delete(temporary, recursive: true);
}

return 0;

static object Asset(
    int id,
    string name,
    long size,
    string releaseBase,
    string assetApiBase,
    string? digest) => new
{
    name,
    url = assetApiBase + id,
    browser_download_url = releaseBase + name,
    size,
    digest
};

file sealed class FakeHandler(
    Uri apiUri,
    byte[] apiJson,
    IReadOnlyDictionary<string, byte[]> assets) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        if (request.RequestUri == apiUri)
        {
            return Task.FromResult(Response(apiJson, "application/json"));
        }

        if (request.RequestUri is not null
            && assets.TryGetValue(request.RequestUri.AbsoluteUri, out var content))
        {
            return Task.FromResult(Response(content, "application/octet-stream"));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Response(byte[] content, string contentType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
        }
    };
}
