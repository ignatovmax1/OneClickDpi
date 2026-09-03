using System.Security.Cryptography;
using System.Text.Json;

return args.FirstOrDefault()?.ToLowerInvariant() switch
{
    "keygen" => GenerateKey(args),
    "channel-keygen" => GenerateChannelKey(args),
    "manifest" => CreateManifest(args),
    "verify" => VerifyManifest(args),
    _ => Usage()
};

static int GenerateChannelKey(string[] args)
{
    if (args.Length != 2)
    {
        return Usage();
    }

    var keyPath = Path.GetFullPath(args[1]);
    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine("Refusing to overwrite an existing channel key.");
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
    Console.WriteLine($"Channel key: {keyPath}");
    return 0;
}

static int GenerateKey(string[] args)
{
    if (args.Length != 3)
    {
        return Usage();
    }

    var privatePath = Path.GetFullPath(args[1]);
    var publicPath = Path.GetFullPath(args[2]);
    if (File.Exists(privatePath) || File.Exists(publicPath))
    {
        Console.Error.WriteLine("Refusing to overwrite an existing signing key.");
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine($"Private key: {privatePath}");
    Console.WriteLine($"Public key:  {publicPath}");
    return 0;
}

static int CreateManifest(string[] args)
{
    if (args.Length != 7)
    {
        return Usage();
    }

    var privatePath = Path.GetFullPath(args[1]);
    var channelKeyPath = Path.GetFullPath(args[2]);
    var packagePath = Path.GetFullPath(args[3]);
    if (!Version.TryParse(args[4].TrimStart('v', 'V'), out var version))
    {
        Console.Error.WriteLine("The release version is invalid.");
        return 2;
    }

    var notesPath = Path.GetFullPath(args[5]);
    var outputDirectory = Path.GetFullPath(args[6]);
    Directory.CreateDirectory(outputDirectory);
    var packageBytes = File.ReadAllBytes(packagePath);
    var channelKey = File.ReadAllBytes(channelKeyPath);
    if (channelKey.Length != 32)
    {
        Console.Error.WriteLine("The update channel key must contain exactly 32 bytes.");
        return 2;
    }

    var magic = "OCDUPD1\0"u8.ToArray();
    var nonce = RandomNumberGenerator.GetBytes(12);
    var encryptedBytes = new byte[magic.Length + nonce.Length + 16 + packageBytes.Length];
    magic.CopyTo(encryptedBytes, 0);
    nonce.CopyTo(encryptedBytes, magic.Length);
    using (var aes = new AesGcm(channelKey, 16))
    {
        aes.Encrypt(
            nonce,
            packageBytes,
            encryptedBytes.AsSpan(magic.Length + nonce.Length + 16),
            encryptedBytes.AsSpan(magic.Length + nonce.Length, 16),
            magic);
    }

    var downloadFileName = Path.GetFileNameWithoutExtension(packagePath) + ".ocdupdate";
    var downloadPath = Path.Combine(outputDirectory, downloadFileName);
    File.WriteAllBytes(downloadPath, encryptedBytes);
    var manifest = new ReleaseManifest(
        SchemaVersion: 2,
        Version: version.ToString(3),
        PackageFileName: Path.GetFileName(packagePath),
        PackageSize: packageBytes.LongLength,
        Sha256: Convert.ToHexString(SHA256.HashData(packageBytes)),
        DownloadFileName: downloadFileName,
        DownloadSize: encryptedBytes.LongLength,
        DownloadSha256: Convert.ToHexString(SHA256.HashData(encryptedBytes)),
        ReleaseNotes: File.Exists(notesPath) ? File.ReadAllText(notesPath).Trim() : string.Empty);
    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });

    using var signingKey = ECDsa.Create();
    signingKey.ImportFromPem(File.ReadAllText(privatePath));
    var signature = signingKey.SignData(
        manifestBytes,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    var manifestPath = Path.Combine(outputDirectory, "OneClickDpi-update.json");
    var signaturePath = Path.Combine(outputDirectory, "OneClickDpi-update.sig");
    File.WriteAllBytes(manifestPath, manifestBytes);
    File.WriteAllText(signaturePath, Convert.ToBase64String(signature));
    CryptographicOperations.ZeroMemory(channelKey);
    CryptographicOperations.ZeroMemory(packageBytes);
    Console.WriteLine($"Manifest:  {manifestPath}");
    Console.WriteLine($"Signature: {signaturePath}");
    Console.WriteLine($"Package:   {downloadPath}");
    Console.WriteLine($"SHA-256:   {manifest.DownloadSha256}");
    return 0;
}

static int VerifyManifest(string[] args)
{
    if (args.Length != 4)
    {
        return Usage();
    }

    var manifestBytes = File.ReadAllBytes(args[2]);
    var signature = Convert.FromBase64String(File.ReadAllText(args[3]).Trim());
    using var publicKey = ECDsa.Create();
    publicKey.ImportFromPem(File.ReadAllText(args[1]));
    var valid = publicKey.VerifyData(
        manifestBytes,
        signature,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    Console.WriteLine(valid ? "Signature valid." : "Signature invalid.");
    return valid ? 0 : 3;
}

static int Usage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ReleaseTool keygen <private.pem> <public.pem>");
    Console.Error.WriteLine("  ReleaseTool channel-keygen <channel.key>");
    Console.Error.WriteLine("  ReleaseTool manifest <private.pem> <channel.key> <package.zip> <version> <notes.txt> <output-dir>");
    Console.Error.WriteLine("  ReleaseTool verify <public.pem> <manifest.json> <manifest.sig>");
    return 64;
}

file sealed record ReleaseManifest(
    int SchemaVersion,
    string Version,
    string PackageFileName,
    long PackageSize,
    string Sha256,
    string DownloadFileName,
    long DownloadSize,
    string DownloadSha256,
    string ReleaseNotes);
