using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace S8Cam;

public sealed class PreviewDecoder : IAsyncDisposable {
    private readonly string? sdpPath;
    private readonly Action<string> log;
    private readonly object lockObj = new();
    private readonly object depacketizerLock = new();
    private Settings settings;
    private Process? decoder;
    private CancellationTokenSource? cts;
    private Task? pumpTask;
    private Task? stdinTask;
    private RtpAnnexBDepacketizer? depacketizer;
    private Channel<byte[]>? stdinChannel;
    private int currentWidth, currentHeight;

    public event Action<byte[], int, int>? FrameReady;
    public bool Alive => decoder?.HasExited == false;
    public bool UsesDirectPipe => string.IsNullOrWhiteSpace(sdpPath);

    public PreviewDecoder(Settings settings, string? sdpPath, Action<string> log) {
        this.settings = settings;
        this.sdpPath = sdpPath;
        this.log = log;
        StartDecoder();
    }

    public void PushRtp(ReadOnlySpan<byte> rtpPacket) {
        var dep = Volatile.Read(ref depacketizer);
        var ch = Volatile.Read(ref stdinChannel);
        if (dep == null || ch == null) return;
        byte[]? au;
        lock (depacketizerLock) {
            au = dep.Push(rtpPacket);
        }
        if (au != null) ch.Writer.TryWrite(au);
    }

    public static (int Width, int Height) PreviewDimensions(Settings s) {
        var outDim = s.OutputDimensions;
        if (outDim.Width >= outDim.Height) {
            // Landscape 16:9 target: 960x540 (sharp preview via hardware decode)
            return (960, 540);
        } else {
            // Portrait 9:16 target: 540x960
            return (540, 960);
        }
    }

    private void StartDecoder() {
        lock (lockObj) {
            StopDecoderCore();
            cts = new CancellationTokenSource();
            var ct = cts.Token;
            var (pw, ph) = PreviewDimensions(settings);
            currentWidth = pw;
            currentHeight = ph;
            var frameSize = pw * ph * 4;

            try {
                var filter = settings.BuildVideoFilter(pw, ph);
                if (UsesDirectPipe) {
                    var codecFmt = settings.Codec == "hevc" ? "hevc" : "h264";
                    var probe = settings.Codec == "hevc" ? "8192" : "4096";
                    lock (depacketizerLock) {
                        depacketizer = new RtpAnnexBDepacketizer(codecFmt);
                    }
                    var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) {
                        SingleReader = true,
                        FullMode = BoundedChannelFullMode.DropOldest
                    });
                    stdinChannel = ch;
                    decoder = Processes.Start(settings.FfmpegPath, [
                        "-hide_banner", "-loglevel", "warning",
                        "-flags", "low_delay",
                        "-thread_type", "slice", "-threads", "4",
                        "-analyzeduration", "0", "-probesize", probe,
                        "-f", codecFmt, "-i", "pipe:0",
                        "-map", "0:v:0", "-an",
                        "-vf", filter,
                        "-pix_fmt", "bgra",
                        "-fps_mode", "passthrough",
                        "-flush_packets", "1",
                        "-f", "rawvideo",
                        "pipe:1"
                    ], line => log("PreviewDecoder · " + line), stdin: true, stdout: true, videoPriority: true);
                    var pStdin = decoder;
                    stdinTask = Task.Run(async () => {
                        try {
                            var stream = pStdin.StandardInput.BaseStream;
                            await foreach (var au in ch.Reader.ReadAllAsync(ct)) {
                                if (pStdin.HasExited) break;
                                await stream.WriteAsync(au, ct);
                                await stream.FlushAsync(ct);
                            }
                        } catch (OperationCanceledException) {
                        } catch (IOException) {
                        } catch (ObjectDisposedException) {
                        } catch (InvalidOperationException) { }
                    }, ct);
                } else {
                    decoder = Processes.Start(settings.FfmpegPath, [
                        "-hide_banner", "-loglevel", "warning", "-nostdin",
                        "-flags", "low_delay", "-thread_type", "slice", "-threads", "4",
                        "-analyzeduration", "50000", "-probesize", "65536",
                        "-protocol_whitelist", "file,udp,rtp",
                        "-buffer_size", "8388608", "-max_delay", "0", "-reorder_queue_size", "0",
                        "-i", sdpPath!,
                        "-map", "0:v:0", "-an",
                        "-vf", filter,
                        "-pix_fmt", "bgra",
                        "-fps_mode", "passthrough",
                        "-flush_packets", "1",
                        "-f", "rawvideo",
                        "pipe:1"
                    ], line => log("PreviewDecoder · " + line), stdout: true, videoPriority: true);
                }

                var proc = decoder;
                pumpTask = Task.Run(async () => {
                    try {
                        await LatestFramePump.RunAsync(proc.StandardOutput.BaseStream, frameSize, frame => {
                            if (!Volatile.Read(ref settings).PrivacyMute) FrameReady?.Invoke(frame, pw, ph);
                        }, ct);
                    } catch (OperationCanceledException) {
                    } catch (EndOfStreamException) {
                    } catch (Exception ex) {
                        if (!ct.IsCancellationRequested) log("PreviewDecoder pump: " + ex.Message);
                    }
                }, ct);
            } catch (Exception ex) {
                log("PreviewDecoder start: " + ex.Message);
            }
        }
    }

    public void UpdateFilter(Settings newSettings) {
        lock (lockObj) {
            settings = newSettings;
            StartDecoder();
        }
    }

    private void StopDecoderCore() {
        stdinChannel?.Writer.TryComplete();
        stdinChannel = null;
        lock (depacketizerLock) { depacketizer = null; }
        cts?.Cancel();
        if (decoder != null) {
            try { if (UsesDirectPipe) decoder.StandardInput.Close(); } catch { }
            Processes.Kill(decoder);
            try { decoder.Dispose(); } catch { }
            decoder = null;
        }
        cts?.Dispose();
        cts = null;
    }

    public async ValueTask DisposeAsync() {
        Task? taskToWait;
        Task? stdinToWait;
        lock (lockObj) {
            taskToWait = pumpTask;
            stdinToWait = stdinTask;
            StopDecoderCore();
        }
        if (stdinToWait != null) {
            try { await stdinToWait.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        if (taskToWait != null) {
            try { await taskToWait.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
