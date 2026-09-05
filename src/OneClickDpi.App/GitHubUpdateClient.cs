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
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"OneClickDpi/{GetVersion()} updater");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    private static string GetVersion()
    {
        var v = typeof(GitHubUpdateClient).Assembly.GetName().Version;
        return v is null ? "0.6" : $"{v.Major}.{v.Minor}";
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

        var releaseTag = release.TagName.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(releaseTag, out var tagVersion))
        {
            return null;
        }

        if (tagVersion <= currentVersion)
        {
            return null;
        }

        var packageAsset = FindZipAsset(release, tagVersion);
        if (packageAsset is null)
        {
            return null;
        }

        var packageUri = ValidateAssetApiUri(packageAsset.ApiUrl);
        var releasePage = ValidateReleasePageUri(release.HtmlUrl);

        var manifest = new UpdateManifest(
            SchemaVersion: 2,
            Version: tagVersion.ToString(3),
            PackageFileName: packageAsset.Name,
            PackageSize: packageAsset.Size,
            Sha256: string.Empty,
            DownloadFileName: packageAsset.Name,
            DownloadSize: packageAsset.Size,
            DownloadSha256: string.Empty,
            ReleaseNotes: release.Body ?? string.Empty);

        return new UpdateRelease(
            tagVersion,
            manifest,
            ManifestBytes: null,
            SignatureBytes: null,
            packageUri,
            releasePage);
    }

    private static GitHubAsset? FindZipAsset(GitHubReleaseResponse release, Version version)
    {
        var versionStr = version.ToString(3);
        var candidates = release.Assets
            .Where(a => a.Name.Contains(versionStr)
                && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase)
                && !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            > 1 => candidates.OrderByDescending(a => a.Size).First(),
            _ => null
        };
    }

    public async Task<PreparedUpdate> DownloadAsync(
        UpdateRelease release,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var versionDirectory = Path.Combine(_updateRoot, "Downloads", release.Version.ToString(3));
        Directory.CreateDirectory(versionDirectory);
        var packagePath = Path.Combine(versionDirectory, release.Manifest.PackageFileName);
        var partialPath = packagePath + ".partial-" + Guid.NewGuid().ToString("N");

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

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long downloaded = 0;
            var totalSize = release.Manifest.DownloadSize;
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
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (totalSize > 0)
                    {
                        progress?.Report((int)Math.Min(100, downloaded * 100 / totalSize));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);

            File.Move(partialPath, packagePath, overwrite: true);
            progress?.Report(100);

            var manifestPath = Path.Combine(versionDirectory, "OneClickDpi-update.json");
            var signaturePath = Path.Combine(versionDirectory, "OneClickDpi-update.sig");
            await File.WriteAllTextAsync(manifestPath, "{}", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(signaturePath, "", cancellationToken).ConfigureAwait(false);

            return new PreparedUpdate(release, packagePath, manifestPath, signaturePath);
        }
        catch
        {
            TryDeleteFile(partialPath);
            TryDeleteFile(packagePath);
            throw;
        }
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

        [JsonPropertyName("body")]
        public string? Body { get; init; }

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
