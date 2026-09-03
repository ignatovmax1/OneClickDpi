using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OneClickDpi.Core;

public static class TelegramMtProtoHandshake
{
    public static async Task<bool> TryAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var nonce = RandomNumberGenerator.GetBytes(16);
        var payload = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), 0);
        var messageId = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), messageId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), 0xBE7E8EF1);
        nonce.CopyTo(payload, 24);

        await stream.WriteAsync(new byte[] { 0xEF, 0x0A }, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var lengthHeader = new byte[1];
        await stream.ReadExactlyAsync(lengthHeader, cancellationToken).ConfigureAwait(false);
        int payloadLength;
        if (lengthHeader[0] == 0x7F)
        {
            var extendedLength = new byte[3];
            await stream.ReadExactlyAsync(extendedLength, cancellationToken).ConfigureAwait(false);
            payloadLength = (extendedLength[0] | (extendedLength[1] << 8) | (extendedLength[2] << 16)) * 4;
        }
        else
        {
            payloadLength = lengthHeader[0] * 4;
        }

        if (payloadLength is < 40 or > 4096)
        {
            return false;
        }

        var response = new byte[payloadLength];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        var constructor = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(20, 4));
        return constructor == 0x05162463 && response.AsSpan(24, 16).SequenceEqual(nonce);
    }
}
