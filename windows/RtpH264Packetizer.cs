using System.Buffers.Binary;
using System.Security.Cryptography;

namespace S8Cam;

public sealed class RtpH264Packetizer {
    private readonly string codec;
    private ushort sequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
    private readonly uint ssrc = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
    private const int MaxPayload = 1200;

    public RtpH264Packetizer(string codec = "h264") {
        this.codec = codec;
    }

    public IReadOnlyList<byte[]> Packetize(ReadOnlyMemory<byte> annexB, long ptsUs) {
        if (codec == "hevc") return PacketizeHevc(annexB, ptsUs);
        return PacketizeH264(annexB, ptsUs);
    }

    private IReadOnlyList<byte[]> PacketizeH264(ReadOnlyMemory<byte> annexB, long ptsUs) {
        var packets = new List<byte[]>();
        var nals = Split(annexB);
        var timestamp = unchecked((uint)(ptsUs * 90 / 1000));
        for (var n = 0; n < nals.Count; n++) {
            var nal = nals[n];
            var markerForNal = n == nals.Count - 1;
            if (nal.Length <= MaxPayload) {
                packets.Add(Packet(nal, timestamp, markerForNal));
                continue;
            }
            var indicator = (byte)((nal[0] & 0xE0) | 28);
            var type = (byte)(nal[0] & 0x1F);
            var offset = 1;
            var first = true;
            while (offset < nal.Length) {
                var count = Math.Min(MaxPayload - 2, nal.Length - offset);
                var payload = new byte[count + 2];
                payload[0] = indicator;
                payload[1] = (byte)(type | (first ? 0x80 : 0) | (offset + count == nal.Length ? 0x40 : 0));
                Buffer.BlockCopy(nal, offset, payload, 2, count);
                var last = offset + count == nal.Length;
                packets.Add(Packet(payload, timestamp, markerForNal && last));
                offset += count; first = false;
            }
        }
        return packets;
    }

    private IReadOnlyList<byte[]> PacketizeHevc(ReadOnlyMemory<byte> annexB, long ptsUs) {
        var packets = new List<byte[]>();
        var nals = Split(annexB);
        var timestamp = unchecked((uint)(ptsUs * 90 / 1000));
        for (var n = 0; n < nals.Count; n++) {
            var nal = nals[n];
            if (nal.Length < 2) continue;
            var markerForNal = n == nals.Count - 1;
            if (nal.Length <= MaxPayload) {
                packets.Add(Packet(nal, timestamp, markerForNal));
                continue;
            }
            var nalType = (byte)((nal[0] >> 1) & 0x3F);
            var offset = 2;
            var first = true;
            while (offset < nal.Length) {
                var count = Math.Min(MaxPayload - 3, nal.Length - offset);
                var payload = new byte[count + 3];
                // RFC 7798 PayloadHdr: Type 49 in bits 6..1
                payload[0] = (byte)((nal[0] & 0x81) | (49 << 1));
                payload[1] = nal[1];
                var last = offset + count == nal.Length;
                payload[2] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | nalType);
                Buffer.BlockCopy(nal, offset, payload, 3, count);
                packets.Add(Packet(payload, timestamp, markerForNal && last));
                offset += count;
                first = false;
            }
        }
        return packets;
    }

    private byte[] Packet(ReadOnlySpan<byte> payload, uint timestamp, bool marker) {
        var result = new byte[12 + payload.Length];
        result[0] = 0x80; result[1] = (byte)(96 | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), ssrc);
        payload.CopyTo(result.AsSpan(12));
        return result;
    }

    private static List<byte[]> Split(ReadOnlyMemory<byte> data) {
        var result = new List<byte[]>();
        var bytes = data.ToArray();
        var at = Start(0, out var prefix);
        while (at >= 0) {
            var begin = at + prefix;
            var next = Start(begin, out var nextPrefix);
            var end = next >= 0 ? next : bytes.Length;
            if (end > begin) result.Add(bytes[begin..end]);
            at = next; prefix = nextPrefix;
        }
        return result;
        int Start(int from, out int prefixLength) {
            for (var i = from; i + 3 < bytes.Length; i++) {
                if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1) { prefixLength = 3; return i; }
                if (i + 4 <= bytes.Length && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1) { prefixLength = 4; return i; }
            }
            prefixLength = 0; return -1;
        }
    }
}
