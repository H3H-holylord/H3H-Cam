using System.Diagnostics;
using System.IO;

namespace S8Cam;

public sealed class PreviewDecoder : IAsyncDisposable {
    private readonly string sdpPath;
    private readonly Action<string> log;
    private readonly object lockObj = new();
    private Settings settings;
    private Process? decoder;
    private CancellationTokenSource? cts;
    private Task? pumpTask;
    private int currentWidth, currentHeight;

    public event Action<byte[], int, int>? FrameReady;
    public bool Alive => decoder?.HasExited == false;

    public PreviewDecoder(Settings settings, string sdpPath, Action<string> log) {
        this.settings = settings;
        this.sdpPath = sdpPath;
        this.log = log;
        StartDecoder();
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
                decoder = Processes.Start(settings.FfmpegPath, [
                    "-hide_banner", "-loglevel", "warning", "-nostdin",
                    // Keep the probe's first IDR instead of discarding its reference frame.
                    "-flags", "low_delay", "-threads", "2",
                    "-analyzeduration", "100000", "-probesize", "100000",
                    "-hwaccel", "auto",
                    "-protocol_whitelist", "file,udp,rtp",
                    "-buffer_size", "8388608", "-max_delay", "30000", "-reorder_queue_size", "128",
                    "-i", sdpPath,
                    "-map", "0:v:0", "-an",
                    "-vf", filter,
                    "-pix_fmt", "bgra",
                    "-fps_mode", "passthrough",
                    "-threads", "2",
                    "-flush_packets", "1",
                    "-f", "rawvideo",
                    "pipe:1"
                ], line => log("PreviewDecoder · " + line), stdout: true, videoPriority: true);

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
        cts?.Cancel();
        if (decoder != null) {
            Processes.Kill(decoder);
            try { decoder.Dispose(); } catch { }
            decoder = null;
        }
        cts?.Dispose();
        cts = null;
    }

    public async ValueTask DisposeAsync() {
        Task? taskToWait;
        lock (lockObj) {
            taskToWait = pumpTask;
            StopDecoderCore();
        }
        if (taskToWait != null) {
            try { await taskToWait.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
