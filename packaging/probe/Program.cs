using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using S8Cam;

// Run from a test extraction, with PATH reduced to System32. Never ships in Portable.
var root = AppContext.BaseDirectory;
var reportDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "H3HCam-Portable-Test-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(reportDir);
var components = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "COMPONENTS.json"))).RootElement;
var tools = new Dictionary<string, string>();
foreach (var group in new[] { "ffmpeg", "adb" }) {
    foreach (var file in components.GetProperty(group).GetProperty("files").EnumerateObject()) {
        var path = Path.Combine(root, "tools", file.Name);
        Assert(File.Exists(path), "Missing dependency: " + file.Name);
        using var stream = File.OpenRead(path);
        Assert(Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Value.GetString(), StringComparison.OrdinalIgnoreCase), "Hash mismatch: " + file.Name);
        if (file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
            var resolved = Path.GetFullPath(ToolPaths.Find(file.Name));
            Assert(resolved.Equals(path, StringComparison.OrdinalIgnoreCase), "Tool resolved outside package: " + resolved);
            tools[file.Name] = resolved;
            await Run(resolved, file.Name == "adb.exe" ? ["version"] : ["-version"], file.Name + "-version");
        }
    }
}
foreach (var name in new[] { "H3HCam Receiver.exe", "libusb-1.0.dll", "H3H-Cam-4.0.5.apk", "tools/virtualcam/obs-virtualcam-module64.dll", "tools/virtualcam/obs-virtualcam-module32.dll", "Установить на телефон.cmd", "licenses/FFmpeg-GPL-3.0.txt", "licenses/FFmpeg-Build-9.0.1.txt", "licenses/Android-Platform-Tools-NOTICE.txt", "licenses/U2NET-Apache-2.0.txt" })
    Assert(File.Exists(Path.Combine(root, name)), "Missing package component: " + name);
using (var model = File.OpenRead(Path.Combine(root, "models", "u2netp.onnx")))
    Assert(Convert.ToHexString(SHA256.HashData(model)).Equals(components.GetProperty("model").GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Model mismatch");

foreach (var codec in new[] { "h264", "hevc" }) {
    var video = Path.Combine(reportDir, "fixture." + codec);
    var raw = Path.Combine(reportDir, "decoded-" + codec + ".nv12");
    await Run(tools["ffmpeg.exe"], ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=60", "-frames:v", "60", "-c:v", codec == "h264" ? "libx264" : "libx265", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p", "-f", codec, video], "encode-" + codec);
    await Run(tools["ffmpeg.exe"], ["-hide_banner", "-loglevel", "error", "-xerror", "-y", "-threads", "1", "-i", video, "-vf", "transpose=clock,hflip,scale=180:320", "-pix_fmt", "nv12", "-f", "rawvideo", raw], "decode-" + codec);
    Assert(new FileInfo(raw).Length == 60L * 320 * 180 * 3 / 2, "Decoded frame count mismatch: " + codec);
    Console.WriteLine("PASS bundled FFmpeg: 60 " + codec + " frames, rotation/scale/NV12");
}
File.WriteAllText(Path.Combine(reportDir, "tools-results.json"), JsonSerializer.Serialize(new { Tools = tools, H264Frames = 60, HevcFrames = 60, LocalComponents = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS Portable: original client ToolPaths resolves bundled tools; dependencies launch; H.264/HEVC decode; APK, camera modules and AI weights present");

void Assert(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
async Task Run(string exe, string[] arguments, string name) {
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(deadline.Token);
        var log = await stdout + "\n" + await stderr;
        File.WriteAllText(Path.Combine(reportDir, name + ".log"), log);
        Assert(process.ExitCode == 0, name + " failed, see diagnostic log");
    } finally {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}
