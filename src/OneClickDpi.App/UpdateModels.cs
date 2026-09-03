namespace OneClickDpi.App;

public sealed record UpdateManifest(
    int SchemaVersion,
    string Version,
    string PackageFileName,
    long PackageSize,
    string Sha256,
    string DownloadFileName,
    long DownloadSize,
    string DownloadSha256,
    string ReleaseNotes);

public sealed record UpdateRelease(
    Version Version,
    UpdateManifest Manifest,
    byte[] ManifestBytes,
    byte[] SignatureBytes,
    Uri PackageUri,
    Uri ReleasePageUri);

public sealed record PreparedUpdate(
    UpdateRelease Release,
    string PackagePath,
    string ManifestPath,
    string SignaturePath);
