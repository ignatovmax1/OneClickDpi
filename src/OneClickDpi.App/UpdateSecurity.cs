using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneClickDpi.App;

public static partial class UpdateSecurity
{
    private const string PublicKeyResourceName = "OneClickDpi.UpdatePublicKey";
    private const string ChannelKeyResourceName = "OneClickDpi.UpdateChannelKey";
    private const long MaximumPackageSize = 350L * 1024 * 1024;
    private static readonly byte[] EncryptedPackageMagic = "OCDUPD1\0"u8.ToArray();

    public static UpdateManifest VerifyAndParseManifest(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes)
    {
        if (manifestBytes.IsEmpty || manifestBytes.Length > 128 * 1024)
        {
            throw new InvalidDataException("Update manifest has an invalid size.");
        }

        if (signatureBytes.Length != 64)
        {
            throw new CryptographicException("Update signature has an invalid size.");
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(ReadPublicKey());
        if (!key.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new CryptographicException("Update signature is invalid.");
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("Update manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    public static async Task VerifyPackageAsync(
        string packagePath,
        UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(packagePath);
        if (!file.Exists || file.Length != manifest.PackageSize)
        {
            throw new InvalidDataException("Downloaded update size does not match the signed manifest.");
        }

        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var expected = Convert.FromHexString(manifest.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(hash, expected))
        {
            throw new CryptographicException("Downloaded update checksum is invalid.");
        }
    }

    public static async Task DecryptPackageAsync(
        string encryptedPath,
        string packagePath,
        UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var encryptedFile = new FileInfo(encryptedPath);
        if (!encryptedFile.Exists || encryptedFile.Length != manifest.DownloadSize)
        {
            throw new InvalidDataException("Encrypted update size does not match the signed manifest.");
        }

        var encrypted = await File.ReadAllBytesAsync(encryptedPath, cancellationToken).ConfigureAwait(false);
        if (encrypted.Length < EncryptedPackageMagic.Length + 12 + 16 + 1
            || !encrypted.AsSpan(0, EncryptedPackageMagic.Length).SequenceEqual(EncryptedPackageMagic))
        {
            throw new CryptographicException("Encrypted update format is invalid.");
        }

        var nonceOffset = EncryptedPackageMagic.Length;
        var tagOffset = nonceOffset + 12;
        var cipherOffset = tagOffset + 16;
        var plaintext = new byte[encrypted.Length - cipherOffset];
        var channelKey = ReadChannelKey();
        try
        {
            using var aes = new AesGcm(channelKey, 16);
            aes.Decrypt(
                encrypted.AsSpan(nonceOffset, 12),
                encrypted.AsSpan(cipherOffset),
                encrypted.AsSpan(tagOffset, 16),
                plaintext,
                EncryptedPackageMagic);

            if (plaintext.LongLength != manifest.PackageSize)
            {
                throw new InvalidDataException("Decrypted update size does not match the signed manifest.");
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
            Directory.CreateDirectory(directory);
            var temporary = packagePath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, plaintext, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, packagePath, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(channelKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        await VerifyPackageAsync(packagePath, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidDataException("Unsupported update manifest version.");
        }

        if (!System.Version.TryParse(manifest.Version, out var version) || version.Major < 0)
        {
            throw new InvalidDataException("Update version is invalid.");
        }

        if (Path.GetFileName(manifest.PackageFileName) != manifest.PackageFileName
            || !PackageNameRegex().IsMatch(manifest.PackageFileName))
        {
            throw new InvalidDataException("Update package name is invalid.");
        }

        if (manifest.PackageSize is <= 0 or > MaximumPackageSize)
        {
            throw new InvalidDataException("Update package size is outside the allowed range.");
        }

        if (manifest.Sha256.Length != 64 || !Sha256Regex().IsMatch(manifest.Sha256))
        {
            throw new InvalidDataException("Update checksum is invalid.");
        }

        if (Path.GetFileName(manifest.DownloadFileName) != manifest.DownloadFileName
            || !DownloadNameRegex().IsMatch(manifest.DownloadFileName))
        {
            throw new InvalidDataException("Encrypted update package name is invalid.");
        }

        if (manifest.DownloadSize is <= 0 or > MaximumPackageSize + 64)
        {
            throw new InvalidDataException("Encrypted update package size is outside the allowed range.");
        }

        if (manifest.DownloadSha256.Length != 64 || !Sha256Regex().IsMatch(manifest.DownloadSha256))
        {
            throw new InvalidDataException("Encrypted update checksum is invalid.");
        }

        if (manifest.ReleaseNotes.Length > 32 * 1024)
        {
            throw new InvalidDataException("Update release notes are too large.");
        }
    }

    private static string ReadPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new InvalidOperationException("Embedded update public key is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ReadChannelKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ChannelKeyResourceName)
            ?? throw new InvalidOperationException("Embedded update channel key is missing.");
        if (stream.Length != 32)
        {
            throw new InvalidOperationException("Embedded update channel key has an invalid size.");
        }

        var key = new byte[32];
        stream.ReadExactly(key);
        return key;
    }

    private static void TryDelete(string path)
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"\AOneClickDpi-MVP-[0-9]+\.[0-9]+\.[0-9]+-win-x64\.zip\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"\AOneClickDpi-MVP-[0-9]+\.[0-9]+\.[0-9]+-win-x64\.ocdupdate\z", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadNameRegex();

    [GeneratedRegex(@"\A[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
