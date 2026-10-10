using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text.Json;
using S8Cam;

internal static class VirtualCameraCaptureTest {
    public static async Task Run(string ffmpeg, string folder) {
        // Do not replace a producer owned by OBS or a running client during this integration test.
        try {
            using var existing = MemoryMappedFile.OpenExisting("OBSVirtualCamVideo");
            using var header = existing.CreateViewAccessor(0, 12, MemoryMappedFileAccess.Read);
            if (header.ReadUInt32(8) is 1 or 2) throw new InvalidOperationException("A virtual-camera producer is already active; stop it before the capture test");
        } catch (FileNotFoundException) { }
        Directory.CreateDirectory(folder);
        const int width = 1920, height = 1080, fps = 60, frames = 30;
        using var writer = new VirtualCameraWriter(width, height, fps);
        var status = VirtualCameraDriver.GetStatus();
        var fixture = new byte[width * height * 3 / 2];
        Array.Fill(fixture, (byte)96, 0, width * height);
        Array.Fill(fixture, (byte)128, width * height, width * height / 2);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (x < width / 4 && y < height / 4) fixture[y * width + x] = 32;
                else if (x >= width * 3 / 4 && y >= height * 3 / 4) fixture[y * width + x] = 200;
        writer.Write(fixture);
        var output = Path.GetFullPath(Path.Combine(folder, "capture.nv12"));
        var start = new ProcessStartInfo(ffmpeg) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "info", "-f", "dshow", "-video_size", $"{width}x{height}",
            "-framerate", fps.ToString(), "-i", "video=" + status.DeviceName, "-frames:v", frames.ToString(),
            "-an", "-pix_fmt", "nv12", "-f", "rawvideo", "-y", output }) start.ArgumentList.Add(arg);
        using var consumer = Process.Start(start) ?? throw new IOException("Could not start capture consumer");
        var errors = consumer.StandardError.ReadToEndAsync();
        var stdout = consumer.StandardOutput.ReadToEndAsync();
        var limit = Stopwatch.StartNew();
        try {
            while (!consumer.HasExited && limit.Elapsed < TimeSpan.FromSeconds(20)) {
                writer.Write(fixture);
                await Task.Delay(15);
            }
            if (!consumer.HasExited) throw new TimeoutException("DirectShow capture consumer did not finish");
            var log = await errors;
            await stdout;
            File.WriteAllText(Path.Combine(folder, "consumer.log"), log);
            if (consumer.ExitCode != 0) throw new IOException("DirectShow capture failed: " + log);
            var captured = File.ReadAllBytes(output);
            if (captured.Length != fixture.Length * frames) throw new IOException("Consumer returned an unexpected frame count");
            for (int i = 0; i < frames; i++)
                if (!captured.AsSpan(i * fixture.Length, fixture.Length).SequenceEqual(fixture))
                    throw new IOException($"Consumer frame {i} differs from the NV12 producer");
            File.WriteAllText(Path.Combine(folder, "capture.json"), JsonSerializer.Serialize(new {
                status.DeviceName, status.DllPath, width, height, requestedFps = fps, frames,
                fixtureSha256 = Convert.ToHexString(SHA256.HashData(fixture)), exactBytes = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS DirectShow capture: {status.DeviceName}, {width}x{height} at requested {fps} FPS, {frames} exact NV12 frames");
        } finally {
            if (!consumer.HasExited) { consumer.Kill(entireProcessTree: true); await consumer.WaitForExitAsync(); }
        }
    }
}
