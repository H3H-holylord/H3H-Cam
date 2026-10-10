using System.Diagnostics;
using System.IO;
using System.Text;
namespace S8Cam;
public static class Processes {
    public static void PrioritizeVideo(Process process, Action<string>? log = null) {
        try {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            log?.Invoke(L.Get("s_7555bcb7f2da") + process.ProcessName);
        } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            log?.Invoke(L.Get("s_deffd1b4b95f") + ex.Message);
        }
    }

    public static Process Start(string file, IEnumerable<string> args, Action<string>? log = null, bool stdin = false, bool stdout = false, bool videoPriority = false) {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardInput = stdin, RedirectStandardOutput = stdout,
            StandardErrorEncoding = Encoding.UTF8 };
        if (stdout) info.StandardOutputEncoding = Encoding.UTF8;
        foreach (var a in args) info.ArgumentList.Add(a);
        var p = new Process { StartInfo = info, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } line) log?.Invoke(line); };
        if (!p.Start()) throw new InvalidOperationException(L.Format("s_9191b7180486", file));
        if (videoPriority) PrioritizeVideo(p, log);
        p.BeginErrorReadLine();
        return p;
    }
    public static async Task<string> Run(string file, IEnumerable<string> args, CancellationToken token, int timeout = 12000) {
        var errors = new StringBuilder();
        using var p = Start(file, args, line => { lock (errors) errors.AppendLine(line); }, stdout: true);
        var output = p.StandardOutput.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try { await p.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            Kill(p); throw new TimeoutException(L.Get("s_0e2b79b96d80"));
        }
        catch { Kill(p); throw; }
        var result = await output;
        // Android 10 ActivityManager returns -1 (255 via adb) for a successful stop.
        var androidStopped = p.ExitCode == 255 && args.Contains("stopservice") && (result + errors).Contains("Service stopped");
        if (p.ExitCode != 0 && !androidStopped) throw new IOException(L.Format("s_c62cb1c4a5e3", p.ExitCode, result.Trim(), errors));
        return result.Trim();
    }
    public static void Kill(Process? p) {
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) {} catch (System.ComponentModel.Win32Exception) {}
    }
}
