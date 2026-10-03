using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace S8Cam;

public enum H3HMessageType : ushort {
    Hello = 1,
    Capabilities = 2,
    Command = 3,
    Ack = 4,
    Error = 5,
    VideoConfig = 6,
    VideoFrame = 7,
    Heartbeat = 8,
    Telemetry = 9,
    HelloAck = 10,
    CameraList = 11,
    Pong = 12,
    SetBitrate = 13,
    RequestIdr = 14,
    Battery = 15,
    Thermal = 16,
    Audio = 17
}

public readonly record struct H3HMessage(H3HMessageType Type, uint Sequence, long TimestampUs,
    uint Flags, byte[] Payload, ushort Version = H3HProtocol.CurrentVersion) {
    public bool IsIdr => (Flags & 1) != 0;
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>Versioned framing shared by USB Direct and future transports with protocol negotiation.</summary>
public static class H3HProtocol {
    public const ushort Version1 = 1;
    public const ushort Version2 = 2;
    public const ushort CurrentVersion = Version2;
    public const int HeaderSize = 32;
    public const int MaxPayload = 16 * 1024 * 1024;
    private static readonly byte[] Magic = "H3HC"u8.ToArray();

    public static byte[] Encode(H3HMessage message, ushort? versionOverride = null) {
        if (message.Payload.Length > MaxPayload) throw new InvalidDataException("H3H payload is too large");
        var packet = new byte[HeaderSize + message.Payload.Length];
        Magic.CopyTo(packet, 0);
        var ver = versionOverride ?? (message.Version != 0 ? message.Version : CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ver);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)message.Type);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), message.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), message.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), message.TimestampUs);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), message.Flags);
        message.Payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    public static H3HMessage Json(H3HMessageType type, uint sequence, object value, uint flags = 0, ushort version = CurrentVersion) =>
        new(type, sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, flags,
            JsonSerializer.SerializeToUtf8Bytes(value), version);

    public static async ValueTask<H3HMessage?> ReadAsync(Stream stream, byte[]? headerBuffer, CancellationToken ct) {
        var header = headerBuffer ?? new byte[HeaderSize];
        if (!await ReadExactAsync(stream, header.AsMemory(0, HeaderSize), ct)) return null;
        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("H3H magic mismatch");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version is not (Version1 or Version2)) throw new InvalidDataException($"Unsupported H3H protocol {version}");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (length is < 0 or > MaxPayload) throw new InvalidDataException("Invalid H3H payload length");
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct)) throw new EndOfStreamException();
        return new H3HMessage((H3HMessageType)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)), payload, version);
    }

    public static ValueTask<H3HMessage?> ReadAsync(Stream stream, CancellationToken ct) =>
        ReadAsync(stream, null, ct);

    public static async ValueTask WriteAsync(Stream stream, H3HMessage message, CancellationToken ct) {
        await stream.WriteAsync(Encode(message), ct);
        await stream.FlushAsync(ct);
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> target, CancellationToken ct) {
        var done = 0;
        while (done < target.Length) {
            var read = await stream.ReadAsync(target[done..], ct);
            if (read == 0) return done == 0 ? false : throw new EndOfStreamException();
            done += read;
        }
        return true;
    }
}
