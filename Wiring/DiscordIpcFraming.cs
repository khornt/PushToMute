using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace PushToMute.Wiring;

// Discord RPC wire-format: 8-byte little-endian header followed by UTF-8 JSON.
internal static class DiscordIpcFraming
{
    internal static async Task WriteFrameAsync(
        NetworkStream stream,
        int opcode,
        object data,
        CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] header = new byte[8];

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    internal static async Task<(int opcode, JsonElement payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (length < 0 || length > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid RPC packet size.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement packet = document.RootElement.Clone();
        return (opcode, packet);
    }

}
