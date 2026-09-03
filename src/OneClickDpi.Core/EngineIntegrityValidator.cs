using System.Security.Cryptography;

namespace OneClickDpi.Core;

public sealed class EngineIntegrityValidator
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["winws.exe"] = "AFFB4F69D2EA302A7ABCCD5325D81826E140DDAE014F1E070BC4A6C0DD555188",
            ["WinDivert.dll"] = "C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2",
            ["WinDivert64.sys"] = "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2",
            ["cygwin1.dll"] = "103104A52E5293CE418944725DF19E2BF81AD9269B9A120D71D39028E821499B"
        };

    public async Task ValidateAsync(EnginePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = new[]
        {
            paths.Executable,
            paths.WinDivertLibrary,
            paths.WinDivertDriver,
            paths.CygwinRuntime
        };

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException(
                    $"Не найден компонент подключения: {Path.GetFileName(file)}. "
                    + "Перезапустите программу. Если файл снова исчезает, проверьте карантин Защитника Windows.",
                    file);
            }

            var expectedHash = ExpectedHashes[Path.GetFileName(file)];
            await using var stream = File.OpenRead(file);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(hash);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Компонент подключения повреждён: {Path.GetFileName(file)}. "
                    + "Перезапустите программу, чтобы восстановить его.");
            }
        }
    }
}
