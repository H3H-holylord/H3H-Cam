using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace S8Cam;

public enum LogLevel {
    Debug,
    Info,
    Warning,
    Error
}

public sealed class RollingLogger : IDisposable {
    public static string LogDir => Path.Combine(Settings.DataDir,"logs");

    private readonly ConcurrentQueue<string> recentErrors = new();
    private readonly object writeLock = new();
    private StreamWriter? currentWriter;
    private string currentDay = "";
    private bool disposed;

    public RollingLogger() {
        try {
            Directory.CreateDirectory(LogDir);
            CleanupOldLogs(keepDays: 5);
        } catch { }
    }

    public void Log(string message, LogLevel level = LogLevel.Info) {
        if (disposed) return;
        var now = DateTime.Now;
        var levelTag = level switch {
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Warning => "[WARN ]",
            LogLevel.Error => "[ERROR]",
            _ => "[INFO ]"
        };
        var formatted = $"{now:yyyy-MM-dd HH:mm:ss.fff} {levelTag} {message}";

        if (level is LogLevel.Warning or LogLevel.Error) {
            recentErrors.Enqueue(formatted);
            while (recentErrors.Count > 50) recentErrors.TryDequeue(out _);
        }

        lock (writeLock) {
            try {
                EnsureWriter(now);
                currentWriter?.WriteLine(formatted);
                currentWriter?.Flush();
            } catch { }
        }
    }

    public string[] GetRecentErrors() => recentErrors.ToArray();

    public static void OpenFolder() {
        try {
            Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo {
                FileName = LogDir,
                UseShellExecute = true
            });
        } catch { }
    }

    private void EnsureWriter(DateTime now) {
        var day = now.ToString("yyyy-MM-dd");
        if (day == currentDay && currentWriter != null) return;

        currentWriter?.Dispose();
        currentWriter = null;
        currentDay = day;

        var logPath = Path.Combine(LogDir, $"h3hcam-{day}.log");
        var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        currentWriter = new StreamWriter(stream, System.Text.Encoding.UTF8);
    }

    private static void CleanupOldLogs(int keepDays) {
        try {
            var dir = new DirectoryInfo(LogDir);
            if (!dir.Exists) return;
            var cutoff = DateTime.Now.Date.AddDays(-keepDays);
            foreach (var file in dir.GetFiles("h3hcam-*.log")) {
                if (file.LastWriteTime < cutoff) {
                    try { file.Delete(); } catch { }
                }
            }
        } catch { }
    }

    public void Dispose() {
        disposed = true;
        lock (writeLock) {
            currentWriter?.Dispose();
            currentWriter = null;
        }
    }
}
