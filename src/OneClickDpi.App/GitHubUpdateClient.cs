using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneClickDpi.App;

public sealed class GitHubUpdateClient : IDisposable
{
    public const string Repository = "ignatovmax1/OneClickDpi-Updates";
    private static readonly Uri DefaultLatestReleaseApi = new(
        $"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly string AllowedAssetApiPrefix =
        $"https://api.github.com/repos/{Repository}/releases/assets/";

    private readonly HttpClient _client;
    private readonly Uri _latestReleaseApi;
    private readonly string _updateRoot;

    public GitHubUpdateClient(
        string updateRoot,
        Uri? latestReleaseApi = null,
        HttpMessageHandler? handler = null)
    {
        _updateRoot = Path.GetFullPath(updateRoot);
        _latestReleaseApi = latestReleaseApi ?? DefaultLatestReleaseApi;
        _client = handler is null
            ? new HttpClient(
                new SocketsHttpHandler
                {
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(20)
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _client.Timeout = TimeSpan.FromMinutes(10);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("OneClickDpi/0.6 updater");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            _latestReleaseApi,
            "application/vnd.github+json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            responseStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");

        if (release.Draft || release.Prerelease)
        {
            return null;
        }

        var manifestAsset = FindAsset(release, "OneClickDpi-update.json");
        var signatureAsset = FindAsset(release, "OneClickDpi-update.sig");
        var manifestBytes = await DownloadSmallAssetAsync(manifestAsset, 128 * 1024, cancellationToken)
            .ConfigureAwait(false);
        var signatureText = await DownloadSmallAssetAsync(signatureAsset, 4 * 1024, cancellationToken)
            .ConfigureAwait(false);
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureText).Trim());
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Update signature asset is malformed.", exception);
        }

        var manifest = UpdateSecurity.VerifyAndParseManifest(manifestBytes, signatureBytes);
        var version = Version.Parse(manifest.Version);
        var releaseTag = release.TagName.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(releaseTag, out var tagVersion) || tagVersion != version)
        {
            throw new InvalidDataException("The signed update version does not match the GitHub release tag.");
        }

        if (version <= currentVersion)
        {
            return null;
        }

        var packageAsset = FindAsset(release, manifest.DownloadFileName);
        if (packageAsset.Size != manifest.DownloadSize)
        {
            throw new InvalidDataException("GitHub package size does not match the signed manifest.");
        }

        if (!string.IsNullOrWhiteSpace(packageAsset.Digest)
            && !packageAsset.Digest.Equals($"sha256:{manifest.DownloadSha256}", StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("GitHub package digest does not match the signed manifest.");
        }

        var packageUri = ValidateAssetApiUri(packageAsset.ApiUrl);
        var releasePage = ValidateReleasePageUri(release.HtmlUrl);
        return new UpdateRelease(
            version,
            manifest,
            manifestBytes,
            signatureBytes,
            packageUri,
            releasePage);
    }

    public async Task<PreparedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var versionDirectory = Path.Combine(_updateRoot, "Downloads", release.Version.ToString(3));
        Directory.CreateDirectory(versionDirectory);
        var packagePath = Path.Combine(versionDirectory, release.Manifest.PackageFileName);
        var encryptedPath = Path.Combine(versionDirectory, release.Manifest.DownloadFileName);
        var partialPath = encryptedPath + ".partial-" + Guid.NewGuid().ToString("N");

        try
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                release.PackageUri,
                "application/octet-stream");
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != release.Manifest.DownloadSize)
            {
                throw new InvalidDataException("Downloaded update has an unexpected size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long downloaded = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    downloaded += read;
                    if (downloaded > release.Manifest.DownloadSize)
                    {
                        throw new InvalidDataException("Downloaded update exceeded its signed size.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((int)Math.Min(100, downloaded * 100 / release.Manifest.DownloadSize));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            if (downloaded != release.Manifest.DownloadSize)
            {
                throw new InvalidDataException("Downloaded update is incomplete.");
            }

            var actualHash = hasher.GetHashAndReset();
            var expectedHash = Convert.FromHexString(release.Manifest.DownloadSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new CryptographicException("Downloaded update checksum is invalid.");
            }

            File.Move(partialPath, encryptedPath, overwrite: true);
            await UpdateSecurity.DecryptPackageAsync(
                encryptedPath,
                packagePath,
                release.Manifest,
                cancellationToken).ConfigureAwait(false);
            TryDeleteFile(encryptedPath);
            var manifestPath = Path.Combine(versionDirectory, "OneClickDpi-update.json");
            var signaturePath = Path.Combine(versionDirectory, "OneClickDpi-update.sig");
            await File.WriteAllBytesAsync(manifestPath, release.ManifestBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                signaturePath,
                Convert.ToBase64String(release.SignatureBytes),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
            return new PreparedUpdate(release, packagePath, manifestPath, signaturePath);
        }
        catch
        {
            TryDeleteFile(partialPath);
            TryDeleteFile(encryptedPath);
            TryDeleteFile(packagePath);
            throw;
        }
    }

    private async Task<byte[]> DownloadSmallAssetAsync(
        GitHubAsset asset,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var uri = ValidateAssetApiUri(asset.ApiUrl);
        using var request = CreateRequest(
            HttpMethod.Get,
            uri,
            "application/octet-stream");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException($"Update asset {asset.Name} is too large.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException($"Update asset {asset.Name} is too large.");
        }

        return bytes;
    }

    private static GitHubAsset FindAsset(GitHubReleaseResponse release, string name)
    {
        var matches = release.Assets.Where(asset => asset.Name.Equals(name, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"GitHub release must contain exactly one {name} asset.");
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        string accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static Uri ValidateAssetApiUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !value.StartsWith(AllowedAssetApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an untrusted update download URL.");
        }

        return uri;
    }

    private static Uri ValidateReleasePageUri(string value)
    {
        var prefix = $"https://github.com/{Repository}/releases/";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an untrusted release page URL.");
        }

        return uri;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _client.Dispose();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("url")]
        public string ApiUrl { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
