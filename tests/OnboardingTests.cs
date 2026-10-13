using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using S8Cam;

internal static class OnboardingTests {
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static async Task Run(string folder) {
        Directory.CreateDirectory(folder);
        var ticket = new WifiPairingTicket();
        Check(!ticket.Claim("bad", IPAddress.Parse("192.168.1.8")), "Wrong QR token rejected");
        Check(!ticket.Claim(ticket.Token, IPAddress.Loopback), "Loopback QR peer rejected");
        Check(ticket.Claim(ticket.Token, IPAddress.Parse("192.168.1.8")), "Valid QR peer accepted");
        Check(!ticket.Claim(ticket.Token, IPAddress.Parse("192.168.1.9")), "Another peer cannot reuse invitation");
        var config = new Settings { Transport = "wifi", WifiPairSession = ticket.Token, Width = 2560, Height = 1440, Fps = 60, BitrateMbps = 32 };
        Check(config.Clone().WifiPairSession == ticket.Token, "Runtime clone preserves session");
        Check(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(config))!.WifiPairSession == "", "Session never persists in JSON");
        var control = Encoding.UTF8.GetString(WifiPairing.Control(new { command = "STOP_STREAM" }, ticket.Token));
        Check(JsonDocument.Parse(control).RootElement.GetProperty("session").GetString() == ticket.Token, "Stop is authenticated");
        var uri = $"h3hcam://wifi?pc=192.168.1.100&token={ticket.Token}";
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(QrCodeGenerator.GenerateBitmap(uri, 9, 4)));
        using (var file = File.Create(Path.Combine(folder, "pairing-qr.png"))) encoder.Save(file);
        File.WriteAllText(Path.Combine(folder, "pairing-qr.txt"), uri);
        using var free = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        free.Dispose();
        using var discovery = new WifiDiscoveryService(() => config, _ => {}, port);
        var invitation = discovery.OpenPairing();
        var calls = 0;
        discovery.PairRequested = request => { calls++; Check(request.Capabilities.Cameras.Count == 1, "Camera capabilities reach PC"); return Task.FromResult(config); };
        discovery.Start();
        var local = IPAddress.Parse(WifiDiscoveryService.ResolveBestLocalIpv4());
        using var client = new UdpClient(new IPEndPoint(local, 0));
        var endpoint = new IPEndPoint(local, port);
        var request = Encoding.UTF8.GetBytes("H3HCAM_PAIR " + JsonSerializer.Serialize(new { token = invitation.Token, capabilities = new PhoneCapabilities { Cameras = [new CameraCapability { Key = "0" }] } }));
        for (var n = 0; n < 2; n++) {
            await client.SendAsync(request, request.Length, endpoint);
            using var timeout = new CancellationTokenSource(3000);
            var response = await client.ReceiveAsync(timeout.Token);
            using var ready = JsonDocument.Parse(Encoding.UTF8.GetString(response.Buffer)[18..]);
            Check(ready.RootElement.GetProperty("ready").GetBoolean(), "Receiver readiness returned");
            var settings = ready.RootElement.GetProperty("settings");
            Check(settings.GetProperty("width").GetInt32() == 2560 && settings.GetProperty("bitrate").GetInt32() == 32000000, "PC resolution and bitrate returned");
        }
        Check(calls == 1, "Lost reply retry does not start another receiver");
        discovery.ClosePairing(invitation);
        await client.SendAsync(request, request.Length, endpoint);
        using (var timeout = new CancellationTokenSource(250)) {
            try { await client.ReceiveAsync(timeout.Token); throw new InvalidOperationException("Closed invitation accepted"); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS QR pairing: validation, peer binding, runtime-only secret, UDP capabilities/settings, idempotent retries, closed invitation");
    }

    public static async Task Live(string folder, string toolFolder) {
        Directory.CreateDirectory(folder);
        var config = new Settings { Transport = "wifi", PcIp = WifiDiscoveryService.ResolveBestLocalIpv4(),
            Preview = false, Obs = false, VirtualCamera = true, SpoutOutput = false,
            Fps = 60, BitrateMbps = 32, ScreenOff = false, AdbPath = "intentionally-unavailable-adb.exe",
            FfmpegPath = Path.Combine(toolFolder, "ffmpeg.exe"), FfplayPath = Path.Combine(toolFolder, "ffplay.exe") };
        ReceiverEngine? engine = null;
        var received = false;
        using var discovery = new WifiDiscoveryService(() => config, Console.WriteLine);
        discovery.PairedTelemetry = (ip, telemetry) => engine?.AcceptWifiTelemetry(ip, telemetry);
        var ticket = discovery.OpenPairing();
        discovery.PairRequested = async request => {
            config.PhoneIp = request.Ip; config.WifiPairSession = request.Token;
            var camera = request.Capabilities.Cameras.First(c => c.Facing is "back" or "Задняя" || c.Key == "0");
            var mode = camera.Modes.First(m => m.Width == 1920 && m.Height == 1080);
            config.CameraKey = camera.Key; config.Fps = mode.Fps.Contains(60) ? 60 : mode.Fps.Max();
            engine = new ReceiverEngine(config, Console.WriteLine);
            engine.StopPairedPhone = () => discovery.StopPairedAsync(request.Token);
            engine.Status += status => { if (status.ReceivedMbps > 0.5 && status.DetectedFps > 10) received = true; };
            await engine.Start();
            return config;
        };
        discovery.Start();
        File.WriteAllText(Path.Combine(folder, "live-qr.txt"), $"h3hcam://wifi?pc={config.PcIp}&token={ticket.Token}");
        try {
            using var timeout = new CancellationTokenSource(60000);
            while (!received) await Task.Delay(250, timeout.Token);
            await Task.Delay(5000);
            config.BitrateMbps = 24;
            await engine!.UpdateControls(config, CancellationToken.None);
            await Task.Delay(3000);
            await engine.Stop();
            Console.WriteLine("PASS live Wi-Fi: phone capabilities, PC-selected 1080p FPS/bitrate, RTP/virtual camera, live controls, stop; ADB path unavailable");
        } finally { if (engine != null) await engine.DisposeAsync(); }
    }
}
