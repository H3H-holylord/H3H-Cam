namespace S8Cam;
public static class MediaCommands {
    public static IReadOnlyList<string> Relay(Settings settings, string sdpPath, int? selectedFps = null) {
        // Both transports terminate as RTP on localhost. Wi-Fi supplies RTP/UDP
        // directly; USB unwraps RFC 4571 without rewriting RTP timestamps, sequence,
        // or SSRC. selectedFps remains in the signature for source compatibility.
        _ = selectedFps;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        if (settings.LowLatency) args.AddRange(["-fflags", "+nobuffer+genpts", "-flags", "low_delay"]);
        else args.AddRange(["-fflags", "+genpts"]);
        args.AddRange(["-analyzeduration", "100000", "-probesize", "100000"]);
        args.AddRange([
            "-protocol_whitelist", "file,udp,rtp", "-buffer_size", "2097152", "-max_delay", settings.LowLatency ? "30000" : "100000",
            "-reorder_queue_size", settings.LowLatency ? "64" : "128", "-i", sdpPath]);
        // The phone already sends SPS/PPS immediately before every IDR.  dump_extra would inject
        // another copy derived by FFmpeg and has caused rare malformed access units with some
        // Samsung Exynos streams at 1080p60/high bitrates.
        args.AddRange(["-map", "0:v:0", "-an", "-c:v", "copy", "-flush_packets", "1",
            "-muxdelay", "0", "-muxpreload", "0", "-mpegts_flags", "+resend_headers+initial_discontinuity", "-f", "mpegts", "pipe:1"]);
        return args;
    }
}
