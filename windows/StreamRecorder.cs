using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace S8Cam;

/// <summary>
/// Studio-grade Direct MP4 Stream Recorder.
/// Muxes the camera bitstream into fragmented MP4 without re-encoding.
/// Stores output in %USERPROFILE%\Videos\H3HCam.
/// </summary>
public sealed class StreamRecorder : IDisposable {
    public static string RecordsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "H3HCam");

    private Process? ffmpegProcess;
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

    public string Start(Settings settings, string sdpPath, string? customDirectory = null) {
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

            var args = new List<string> {
                "-hide_banner", "-loglevel", "warning",
                "-protocol_whitelist", "file,udp,rtp",
                "-listen_timeout", "3",
                "-analyzeduration", "1000000",
                "-probesize", "1000000",
                "-buffer_size", "2097152", "-max_delay", "30000", "-reorder_queue_size", "64",
                "-i", sdpPath,
                "-map", "0:v:0",
                "-c:v", "copy",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
                "-flush_packets", "1",
                "-n", currentFilePath
            };

            ffmpegProcess = Processes.Start(ffmpegPath, args, line => log("Recorder · " + line), stdin: true, stdout: false);
            stopwatch.Restart();
            log(L.Format("s_89a18f894601", fileName));
            return currentFilePath;
        }
    }

    public (string FilePath, TimeSpan Duration, long FileSize) Stop() {
        Process? procToStop;
        string path;
        TimeSpan duration;

        lock (lockObj) {
            if (ffmpegProcess == null) {
                stopwatch.Stop();
                return (currentFilePath ?? "", TimeSpan.Zero, 0);
            }
            procToStop = ffmpegProcess;
            ffmpegProcess = null;
            stopwatch.Stop();
            duration = stopwatch.Elapsed;
            path = currentFilePath ?? "";
        }

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
