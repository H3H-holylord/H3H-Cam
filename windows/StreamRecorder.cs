using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace S8Cam;

/// <summary>
/// Студийный прямой рекордер потока в фрагментированный MP4 без перекодирования видео (H.264/HEVC copy)
/// и с опциональной дорожкой микрофона телефона (48 кГц PCM -> AAC 192 kbps).
/// Сохраняет записи в %USERPROFILE%\Videos\H3HCam.
/// </summary>
public sealed class StreamRecorder : IDisposable {
    public static string RecordsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "H3HCam");

    private Process? ffmpegProcess;
    private NamedPipeServerStream? audioPipe;
    private CancellationTokenSource? audioCts;
    private Task? audioPumpTask;
    private Action<ReadOnlyMemory<byte>, int>? audioHandler;
    private PhoneAudioEngine? attachedAudio;
    private readonly Stopwatch stopwatch = new();
    private string? currentFilePath;
    private readonly Action<string> log;
    private readonly object lockObj = new();

    public bool IsRecording {
        get {
            lock (lockObj) {
                return ffmpegProcess != null && !ffmpegProcess.HasExited;
            }
        }
    }

    public TimeSpan Elapsed => stopwatch.Elapsed;
    public string? CurrentFilePath => currentFilePath;

    public StreamRecorder(Action<string> log) {
        this.log = log;
    }

    public string Start(Settings settings, string sdpPath, string? customDirectory = null, PhoneAudioEngine? audioEngine = null) {
        lock (lockObj) {
            if (IsRecording) throw new InvalidOperationException(L.Get("s_d555b614be96"));

            var targetDir = customDirectory ?? RecordsDirectory;
            Directory.CreateDirectory(targetDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"H3H_Record_{timestamp}.mp4";
            currentFilePath = Path.Combine(targetDir, fileName);

            var ffmpegPath = ToolPaths.Find("ffmpeg.exe", settings.FfmpegPath);
            if (!File.Exists(ffmpegPath))
                throw new FileNotFoundException(L.Get("s_145816cdaeaf"), ffmpegPath);

            bool recordAudio = settings.AudioEnabled && audioEngine is { Active: true };
            string? audioPipePath = null;
            var pcmQueue = new ConcurrentQueue<byte[]>();

            if (recordAudio && audioEngine != null) {
                string pipeName = $"h3hcam_recaud_{Guid.NewGuid():N}";
                audioPipePath = $@"\\.\pipe\{pipeName}";
                audioPipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    64 * 1024,
                    64 * 1024);

                audioCts = new CancellationTokenSource();
                var ct = audioCts.Token;
                var pipeRef = audioPipe;
                attachedAudio = audioEngine;
                audioHandler = (pcm, _) => {
                    if (pcmQueue.Count < 120) {
                        pcmQueue.Enqueue(pcm.ToArray());
                    }
                };
                audioEngine.PcmReady += audioHandler;
                audioPumpTask = Task.Run(async () => {
                    try {
                        await pipeRef.WaitForConnectionAsync(ct);
                        var silence10ms = new byte[960];
                        long lastWrite = Environment.TickCount64;
                        while (!ct.IsCancellationRequested && pipeRef.IsConnected) {
                            if (pcmQueue.TryDequeue(out var chunk)) {
                                await pipeRef.WriteAsync(chunk, ct);
                                lastWrite = Environment.TickCount64;
                            } else {
                                if (Environment.TickCount64 - lastWrite > 60) {
                                    await pipeRef.WriteAsync(silence10ms, ct);
                                    lastWrite = Environment.TickCount64;
                                }
                                await Task.Delay(5, ct);
                            }
                        }
                    } catch { }
                }, ct);
            }

            var args = new List<string> {
                "-hide_banner", "-loglevel", "warning",
                "-protocol_whitelist", "file,udp,rtp,pipe",
                "-listen_timeout", "3",
                "-analyzeduration", "1000000",
                "-probesize", "1000000",
                "-buffer_size", "2097152", "-max_delay", "30000", "-reorder_queue_size", "64",
                "-i", sdpPath
            };

            if (recordAudio && audioPipePath != null) {
                args.AddRange([
                    "-f", "s16le", "-ar", "48000", "-ac", "1",
                    "-i", audioPipePath,
                    "-map", "0:v:0",
                    "-map", "1:a:0",
                    "-c:v", "copy",
                    "-c:a", "aac", "-b:a", "192k"
                ]);
            } else {
                args.AddRange([
                    "-map", "0:v:0",
                    "-c:v", "copy"
                ]);
            }

            args.AddRange([
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
                "-flush_packets", "1",
                "-n", currentFilePath
            ]);

            ffmpegProcess = Processes.Start(ffmpegPath, args, line => log("Recorder · " + line), stdin: true, stdout: false);
            stopwatch.Restart();
            log(L.Format("s_89a18f894601", fileName) + (recordAudio ? " [+AUDIO]" : ""));
            return currentFilePath;
        }
    }

    public (string FilePath, TimeSpan Duration, long FileSize) Stop() {
        Process? procToStop;
        NamedPipeServerStream? pipeToClose;
        CancellationTokenSource? ctsToCancel;
        Task? pumpToWait;
        string path;
        TimeSpan duration;

        lock (lockObj) {
            if (attachedAudio != null && audioHandler != null) {
                attachedAudio.PcmReady -= audioHandler;
            }
            attachedAudio = null;
            audioHandler = null;
            pipeToClose = audioPipe;
            audioPipe = null;
            ctsToCancel = audioCts;
            audioCts = null;
            pumpToWait = audioPumpTask;
            audioPumpTask = null;

            if (ffmpegProcess == null) {
                stopwatch.Stop();
                ctsToCancel?.Cancel();
                ctsToCancel?.Dispose();
                pipeToClose?.Dispose();
                return (currentFilePath ?? "", TimeSpan.Zero, 0);
            }
            procToStop = ffmpegProcess;
            ffmpegProcess = null;
            stopwatch.Stop();
            duration = stopwatch.Elapsed;
            path = currentFilePath ?? "";
        }

        ctsToCancel?.Cancel();
        try { pipeToClose?.Dispose(); } catch { }
        if (pumpToWait != null) {
            try { pumpToWait.Wait(500); } catch { }
        }
        ctsToCancel?.Dispose();

        bool interrupted = false;
        try {
            if (!procToStop.HasExited) {
                try {
                    // FFmpeg reads individual command bytes; bypass a StreamWriter BOM.
                    procToStop.StandardInput.BaseStream.WriteByte((byte)'q');
                    procToStop.StandardInput.BaseStream.Flush();
                } catch { }

                if (!procToStop.WaitForExit(4500)) {
                    interrupted = true;
                    Processes.Kill(procToStop);
                }
            }
            if (procToStop.HasExited && procToStop.ExitCode != 0) interrupted = true;
        } catch { interrupted = true; }
        finally {
            procToStop.Dispose();
        }

        long size = 0;
        try {
            if (File.Exists(path)) {
                size = new FileInfo(path).Length;
            }
        } catch { }

        double mb = size / (1024.0 * 1024.0);
        log(L.Format("s_0b02daaec05a", (interrupted ? L.Get("s_a2f86599a54b") : L.Get("s_14da4d45c2cb")), Path.GetFileName(path), mb, duration));
        return (path, duration, size);
    }

    public void Dispose() {
        if (IsRecording) {
            Stop();
        }
    }
}
