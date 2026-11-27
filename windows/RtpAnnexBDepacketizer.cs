using System.Buffers.Binary;
using System.IO;

namespace S8Cam;

/// <summary>
/// Депакетизатор RTP (H.264 RFC 6184 и HEVC RFC 7798) в поток Annex-B с маркером конца кадра (AUD).
/// Передача готовых Access Units с AUD напрямую в stdin FFmpeg устраняет пробинг SDP,
/// локальный UDP-хоп и ожидание начала следующего кадра в парсере H.264/HEVC.
/// </summary>
public sealed class RtpAnnexBDepacketizer {
    private static readonly byte[] StartCode = [0, 0, 0, 1];
    // H.264 Access Unit Delimiter (NAL type 9, primary_pic_type = 7 -> 0xF0)
    private static readonly byte[] AudH264 = [0, 0, 0, 1, 0x09, 0xF0];
    // HEVC Access Unit Delimiter (NAL type 35 -> 0x46 0x01, pic_type = 2 -> 0x50)
    private static readonly byte[] AudHevc = [0, 0, 0, 1, 0x46, 0x01, 0x50];

    private readonly bool isHevc;
    private readonly MemoryStream unit = new(128 * 1024);
    private readonly MemoryStream fragment = new(128 * 1024);
    private bool inFragment;
    private bool hasKeyframeNal;
    private bool seenConfigNal;
    private bool waitingKeyframe = true;
    private uint? activeTimestamp;

    public RtpAnnexBDepacketizer(string codec = "h264") {
        isHevc = string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase);
    }

    public void Reset() {
        unit.SetLength(0);
        fragment.SetLength(0);
        inFragment = false;
        hasKeyframeNal = false;
        seenConfigNal = false;
        waitingKeyframe = true;
        activeTimestamp = null;
    }

    /// <summary>
    /// Принимает один RTP-пакет. Когда приходит маркер конца кадра (M=1), возвращает готовый
    /// буфер Access Unit + AUD; иначе возвращает null.
    /// </summary>
    public byte[]? Push(ReadOnlySpan<byte> rtpPacket) {
        if (rtpPacket.Length < 12 || (rtpPacket[0] >> 6) != 2) return null;

        int csrcCount = rtpPacket[0] & 0x0F;
        bool hasExtension = (rtpPacket[0] & 0x10) != 0;
        bool hasPadding = (rtpPacket[0] & 0x20) != 0;
        bool marker = (rtpPacket[1] & 0x80) != 0;
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.Slice(4, 4));

        int offset = 12 + csrcCount * 4;
        if (offset > rtpPacket.Length) return null;
        if (hasExtension) {
            if (offset + 4 > rtpPacket.Length) return null;
            int extWords = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.Slice(offset + 2, 2));
            offset += 4 + extWords * 4;
        }
        int end = rtpPacket.Length;
        if (hasPadding) {
            if (end <= offset) return null;
            int pad = rtpPacket[end - 1];
            end -= pad;
        }
        if (end <= offset) return null;

        // Если сменился RTP timestamp без marker предыдущего кадра — сбрасываем неполный кадр.
        if (activeTimestamp.HasValue && activeTimestamp.Value != timestamp) {
            unit.SetLength(0);
            fragment.SetLength(0);
            inFragment = false;
            hasKeyframeNal = false;
        }
        activeTimestamp = timestamp;

        var payload = rtpPacket.Slice(offset, end - offset);
        if (isHevc) ProcessHevc(payload);
        else ProcessH264(payload);

        if (!marker) return null;

        byte[]? result = null;
        if (unit.Length > 0 && !inFragment) {
            bool ready = !waitingKeyframe || (seenConfigNal && hasKeyframeNal);
            if (ready) {
                waitingKeyframe = false;
                var aud = isHevc ? AudHevc : AudH264;
                unit.Write(aud, 0, aud.Length);
                result = unit.ToArray();
            }
        }

        unit.SetLength(0);
        fragment.SetLength(0);
        inFragment = false;
        hasKeyframeNal = false;
        activeTimestamp = null;
        return result;
    }

    private void ProcessH264(ReadOnlySpan<byte> payload) {
        int nalType = payload[0] & 0x1F;
        if (nalType is >= 1 and <= 23) {
            inFragment = false;
            fragment.SetLength(0);
            if (nalType == 7) seenConfigNal = true;
            if (nalType == 5) hasKeyframeNal = true;
            WriteNal(payload);
        } else if (nalType == 24) { // STAP-A
            inFragment = false;
            fragment.SetLength(0);
            int pos = 1;
            while (pos + 2 <= payload.Length) {
                int len = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
                pos += 2;
                if (len <= 0 || pos + len > payload.Length) break;
                var nalu = payload.Slice(pos, len);
                int t = nalu[0] & 0x1F;
                if (t == 7) seenConfigNal = true;
                if (t == 5) hasKeyframeNal = true;
                WriteNal(nalu);
                pos += len;
            }
        } else if (nalType == 28) { // FU-A
            if (payload.Length < 3) return;
            byte fuHeader = payload[1];
            bool start = (fuHeader & 0x80) != 0;
            bool end = (fuHeader & 0x40) != 0;
            int origType = fuHeader & 0x1F;
            if (start) {
                fragment.SetLength(0);
                fragment.Write(StartCode, 0, StartCode.Length);
                byte reconstructed = (byte)((payload[0] & 0xE0) | origType);
                fragment.WriteByte(reconstructed);
                fragment.Write(payload.Slice(2));
                inFragment = true;
                if (origType == 7) seenConfigNal = true;
                if (origType == 5) hasKeyframeNal = true;
            } else if (inFragment) {
                fragment.Write(payload.Slice(2));
            }
            if (end && inFragment) {
                fragment.WriteTo(unit);
                fragment.SetLength(0);
                inFragment = false;
            }
        }
    }

    private void ProcessHevc(ReadOnlySpan<byte> payload) {
        if (payload.Length < 2) return;
        int nalType = (payload[0] >> 1) & 0x3F;
        if (nalType <= 47) {
            inFragment = false;
            fragment.SetLength(0);
            if (nalType is 32 or 33) seenConfigNal = true;
            if (nalType is >= 19 and <= 21) hasKeyframeNal = true;
            WriteNal(payload);
        } else if (nalType == 48) { // AP
            inFragment = false;
            fragment.SetLength(0);
            int pos = 2;
            while (pos + 2 <= payload.Length) {
                int len = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
                pos += 2;
                if (len < 2 || pos + len > payload.Length) break;
                var nalu = payload.Slice(pos, len);
                int t = (nalu[0] >> 1) & 0x3F;
                if (t is 32 or 33) seenConfigNal = true;
                if (t is >= 19 and <= 21) hasKeyframeNal = true;
                WriteNal(nalu);
                pos += len;
            }
        } else if (nalType == 49) { // FU
            if (payload.Length < 4) return;
            byte fuHeader = payload[2];
            bool start = (fuHeader & 0x80) != 0;
            bool end = (fuHeader & 0x40) != 0;
            int origType = fuHeader & 0x3F;
            if (start) {
                fragment.SetLength(0);
                fragment.Write(StartCode, 0, StartCode.Length);
                byte hdr0 = (byte)((payload[0] & 0x81) | (origType << 1));
                byte hdr1 = payload[1];
                fragment.WriteByte(hdr0);
                fragment.WriteByte(hdr1);
                fragment.Write(payload.Slice(3));
                inFragment = true;
                if (origType is 32 or 33) seenConfigNal = true;
                if (origType is >= 19 and <= 21) hasKeyframeNal = true;
            } else if (inFragment) {
                fragment.Write(payload.Slice(3));
            }
            if (end && inFragment) {
                fragment.WriteTo(unit);
                fragment.SetLength(0);
                inFragment = false;
            }
        }
    }

    private void WriteNal(ReadOnlySpan<byte> nal) {
        unit.Write(StartCode, 0, StartCode.Length);
        unit.Write(nal);
    }
}
