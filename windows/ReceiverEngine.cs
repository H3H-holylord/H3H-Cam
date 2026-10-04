using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace S8Cam;

public sealed record FaceTrackingInfo(float X, float Y, float Width, float Height, float CropCx, float CropCy, float CropZoom, int FaceCount = 1);

public sealed record LiveStatus(string State, string Serial, string Transport, string Resolution, int RequestedFps,
    double DetectedFps, double ReceivedMbps, double EncodedMbps, long Packets, TimeSpan Elapsed, string Ffmpeg, string Thermal, string Details, PowerTelemetry Power,
    long LostPackets = 0, long RecoveredPackets = 0, long DroppedFrames = 0, double LatencyMs = 0, FaceTrackingInfo? Face = null,
    TransportState TransportState = TransportState.Connected);

public sealed class ReceiverEngine(Settings initialSettings, Action<string> log) : IAsyncDisposable {
    private Settings settings = initialSettings.Clone();
    private Settings requestedSettings = initialSettings.Clone();
    private readonly ConnectionManager connectionManager = new(log);
    private readonly StreamRecorder recorder = new(log);
    private string recordingSdpPath = "";
    private IPEndPoint? recordingEndpoint;
    private bool recordingBootstrapped;
    private int recordingGeneration;
    public ConnectionManager ConnectionManager => connectionManager;
    public bool IsRecording => recorder.IsRecording;
    public TimeSpan RecordElapsed => recorder.Elapsed;
    public string? RecordFilePath => recorder.CurrentFilePath;

    public string StartRecording(string? customDir = null) {
        if (!Running || string.IsNullOrEmpty(sdpPath))
            throw new InvalidOperationException("Поток не активен для записи");
        if (settings.PrivacyMute) throw new InvalidOperationException("Сначала выключите паузу приватности");
        if (recorder.IsRecording) throw new InvalidOperationException("Запись уже активна");
        int port;
        do { port = FreeUdpPair(); }
        while (Math.Abs(port - ingestPort) < 2 || Math.Abs(port - previewIngestPort) < 2 || Math.Abs(port - virtualIngestPort) < 2);
        recordingSdpPath = sdpPath + ".recording.sdp";
        File.WriteAllText(recordingSdpPath, File.ReadAllText(sdpPath).Replace($"m=video {ingestPort} ", $"m=video {port} "));
        recordingBootstrapped = false;
        recordingGeneration = Volatile.Read(ref sourceGeneration);
        var path = recorder.Start(settings, recordingSdpPath, customDir);
        recordingEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        RequestIdr();
        return path;
    }

    public (string FilePath, TimeSpan Duration, long FileSize) StopRecording() {
        var result = recorder.Stop();
        recordingEndpoint = null;
        if (recordingSdpPath.Length > 0) {
            try { File.Delete(recordingSdpPath); } catch (IOException) { }
            recordingSdpPath = "";
        }
        return result;
    }
    private int recoveryAttempt;
    private sealed class RtpSessionState {
        public uint? Ssrc { get; set; }
        public uint? Timestamp { get; set; }
        public ushort? ExpectedSequence { get; set; }
        public HashSet<ushort> Missing { get; } = [];
    }

    private sealed class RtpProbeState {
        public uint? Ssrc { get; set; }
        public int Packets { get; set; }
        public bool Vps { get; set; }
        public bool Sps { get; set; }
        public bool Pps { get; set; }
        public bool Idr { get; set; }
        public bool Marker { get; set; }
        public bool Ready(string codec) => Packets >= 3 && Sps && Pps && Idr && Marker && (codec != "hevc" || Vps);

        public void StartSession(uint ssrc) {
            Ssrc = ssrc;
            Packets = 0;
            Vps = Sps = Pps = Idr = Marker = false;
        }
    }

    private readonly AdbController adb = new(initialSettings, log);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? lifetime, inputLifetime;
    private UdpClient? incoming, forward, output, control;
    private IPEndPoint? controlEndpoint;
    private TcpListener? listener;
    private TcpClient? usbClient;
    private AoaController? aoa;
    private Task? inputTask;
    private Process? ffmpeg, ffplay, logcat;
    private PreviewDecoder? previewDecoder;
    private readonly List<Task> tasks = [];
    private readonly object taskLock = new();
    private string sdpPath = "";
    private string previewSdpPath = "";
    private int previewIngestPort;
    private string virtualSdpPath = "";
    private int virtualIngestPort, virtualGeneration;
    private VirtualCameraOutput? virtualCamera;
    private IPEndPoint? previewIngestEndpoint, virtualIngestEndpoint, relayIngestEndpoint;
    private volatile bool relayBootstrapped, previewBootstrapped, virtualBootstrapped;
    private string effectiveTransport = "wifi";
    private string logcatSerial = "";
    private int ingestPort;
    private int selectedFps;
    private int sourceGeneration, relayGeneration, previewGeneration;
    private long received, frames, packets, lastReceive, lastOutput, lastTelemetry, lastLogcatStart;
    private long lostPackets, recoveredPackets, nackRequests, oldLostPackets, oldRecoveredPackets;
    private long droppedFrames;
    private double rtpTransitMinOffset = double.MaxValue;
    private long lastOffsetReset;
    private double estimatedLatencyMs = 0.0; // relative RTP transit variation, not glass-to-glass latency
    private long lastIdrRequestTick = 0;
    private long oldBytes, oldFrames;
    private readonly Stopwatch elapsed = new();
    private string androidState = "Ожидание камеры", thermal = "—", details = "";
    private double encodedMbps;
    private PowerTelemetry power = new();
    private string resolution = "—";
    private FaceTrackingInfo? lastFace;
    private bool screenSlept;
    private int adaptiveBitrate, stableSeconds;
    private int modeSampleCount;
    private string modeVerification = "проверка режима…";
    private IPAddress? expectedPhoneAddress;

    public event Action<LiveStatus>? Status;
    public event Action<bool>? PreviewChanged;
    public event Action<byte[], int, int>? PreviewFrame;
    public bool Running => lifetime != null;
    public bool RequiresRestart(Settings next) => requestedSettings.RequiresStreamRestart(next);
    public long SpoutFrames => virtualCamera?.SpoutFrames ?? 0;
    public long GpuDroppedFrames => virtualCamera?.GpuDroppedFrames ?? 0;
    public int? VirtualDecoderProcessId => virtualCamera?.DecoderProcessId;
    public event Action? OutputFramePublished;
    public bool PreviewEnabled => settings.Preview;
    private volatile bool previewActive = true;
    public void SetPreviewActivity(bool active) {
        previewActive = active;
        virtualCamera?.SetPreviewEnabled(settings.Preview && active);
    }
    private bool SharedPreview => virtualCamera?.CanSharePreview == true;
    public int? PreviewProcessId => settings.Preview && SharedPreview ? 1 : previewDecoder?.Alive == true ? 1 : ffplay?.Id;

    private VirtualCameraOutput CreateVirtualCamera(CancellationToken ct) {
        var output = new VirtualCameraOutput(settings, virtualSdpPath, ct, log);
        output.SetPreviewEnabled(settings.Preview && previewActive);
        output.PreviewFrame += (frame, w, h) => {
            if (settings.Preview && ReferenceEquals(virtualCamera, output)) PreviewFrame?.Invoke(frame, w, h);
        };
        output.FramePublished += () => {
            if (ReferenceEquals(virtualCamera, output)) OutputFramePublished?.Invoke();
        };
        return output;
    }

    public void SetPreviewEnabled(bool enabled) {
        settings.Preview = enabled;
        virtualCamera?.SetPreviewEnabled(enabled && previewActive);
        if (enabled) StartPreview();
        else StopPreview();
    }
    public async Task UpdateControls(Settings nextSettings, CancellationToken ct) {
        await lifecycle.WaitAsync(ct);
        try {
            if (!Running || lifetime!.IsCancellationRequested) return;
            await UpdateControlsCore(nextSettings, lifetime.Token);
        } finally { lifecycle.Release(); }
    }

    private async Task UpdateControlsCore(Settings nextSettings, CancellationToken ct) {
        var oldSettings = settings;
        requestedSettings = nextSettings.Clone();
        var outputRoutingChanged = oldSettings.VirtualCamera != nextSettings.VirtualCamera ||
                                   oldSettings.SpoutOutput != nextSettings.SpoutOutput;
        var filterChanged = oldSettings.FlipHorizontal != nextSettings.FlipHorizontal ||
                            oldSettings.Rotation != nextSettings.Rotation ||
                            oldSettings.PrivacyMute != nextSettings.PrivacyMute ||
                            oldSettings.OrientationMode != nextSettings.OrientationMode ||
                            oldSettings.SuperResolution4K != nextSettings.SuperResolution4K ||
                            outputRoutingChanged;
        settings = nextSettings.Clone();
        if (settings.PrivacyMute && recorder.IsRecording) StopRecording();
        // UI stores empty paths for automatic discovery; retain the resolved runtime paths.
        if (string.IsNullOrWhiteSpace(settings.AdbPath)) settings.AdbPath = oldSettings.AdbPath;
        if (string.IsNullOrWhiteSpace(settings.FfmpegPath)) settings.FfmpegPath = oldSettings.FfmpegPath;
        if (string.IsNullOrWhiteSpace(settings.FfplayPath)) settings.FfplayPath = oldSettings.FfplayPath;
        if (settings.AutoPcIp) settings.PcIp = oldSettings.PcIp;
        adb.UpdateSettings(settings);
        virtualCamera?.UpdateSettings(settings);
        virtualCamera?.SetPreviewEnabled(settings.Preview && previewActive);

        // Reset adaptive bitrate to requested setting so it never gets stuck at floor
        var targetBitrate = nextSettings.EffectiveBitrateMbps(effectiveTransport) * 1_000_000;
        adaptiveBitrate = targetBitrate;
        stableSeconds = 0;

        if (filterChanged) {
            if (!outputRoutingChanged || oldSettings.FlipHorizontal != settings.FlipHorizontal ||
                oldSettings.Rotation != settings.Rotation || oldSettings.PrivacyMute != settings.PrivacyMute ||
                oldSettings.OrientationMode != settings.OrientationMode || oldSettings.SuperResolution4K != settings.SuperResolution4K) {
                previewBootstrapped = false;
                previewDecoder?.UpdateFilter(settings);
            }
            if (virtualCamera != null || settings.VirtualCamera || settings.SpoutOutput) {
                {
                    var old = virtualCamera;
                    virtualCamera = null;
                    virtualBootstrapped = false;
                    if (old != null) await old.DisposeAsync();
                    if (settings.VirtualCamera || settings.SpoutOutput) {
                        try {
                            virtualCamera = CreateVirtualCamera(ct);
                            virtualGeneration = Volatile.Read(ref sourceGeneration);
                        } catch (Exception ex) {
                            log("⚠️ Виртуальная камера недоступна: " + ex.Message);
                            virtualCamera = null;
                        }
                    }
                }
            }
            if (ffplay != null && !ffplay.HasExited) {
                Processes.Kill(ffplay);
                ffplay?.Dispose();
                ffplay = null;
                LaunchExternalFfplay();
            }
            if (settings.Preview) StartPreview();
            RequestIdr();
        }

        var deviceSettingsChanged =
            oldSettings.BitrateMbps != nextSettings.BitrateMbps ||
            oldSettings.Fps != nextSettings.Fps ||
            oldSettings.Width != nextSettings.Width ||
            oldSettings.Height != nextSettings.Height ||
            oldSettings.Codec != nextSettings.Codec ||
            oldSettings.Focus != nextSettings.Focus ||
            Math.Abs(oldSettings.FocusDistance - nextSettings.FocusDistance) > 0.01f ||
            oldSettings.Exposure != nextSettings.Exposure ||
            oldSettings.Wb != nextSettings.Wb ||
            Math.Abs(oldSettings.Zoom - nextSettings.Zoom) > 0.01f ||
            oldSettings.Torch != nextSettings.Torch ||
            oldSettings.LockAeAwb != nextSettings.LockAeAwb ||
            oldSettings.FaceTracking != nextSettings.FaceTracking ||
            oldSettings.AutoFraming != nextSettings.AutoFraming ||
            oldSettings.AutoFramingZoom != nextSettings.AutoFramingZoom ||
            oldSettings.AutoFramingSpeed != nextSettings.AutoFramingSpeed ||
            oldSettings.AutoFramingDeadzone != nextSettings.AutoFramingDeadzone ||
            oldSettings.PowerMode != nextSettings.PowerMode ||
            oldSettings.CameraKey != nextSettings.CameraKey ||
            oldSettings.ForceSamsungLegacy != nextSettings.ForceSamsungLegacy ||
            oldSettings.ManualIso != nextSettings.ManualIso ||
            oldSettings.ShutterSpeedNs != nextSettings.ShutterSpeedNs ||
            oldSettings.ManualWbKelvin != nextSettings.ManualWbKelvin ||
            oldSettings.Stabilization != nextSettings.Stabilization ||
            oldSettings.StabilizationMode != nextSettings.StabilizationMode;

        if (effectiveTransport == "usb" && oldSettings.BatteryProtect != nextSettings.BatteryProtect) {
            await adb.SetBatteryCharging(!nextSettings.BatteryProtect, ct);
        }

        if (oldSettings.PrivacyMute && !nextSettings.PrivacyMute) {
            // A pause deliberately skips RTP sequence numbers. Start a clean local relay
            // instead of making its jitter buffer interpret the gap as thousands of losses.
            if (ffmpeg != null) await RestartFfmpeg(ct);
            relayBootstrapped = previewBootstrapped = virtualBootstrapped = false;
            RequestIdr();
        }
        if (!deviceSettingsChanged) return;

        try {
            if (effectiveTransport is "wifi" or "usb") {
                // Use the established, reliable control connection for user settings.
                // UDP feedback remains for time-critical NACK/IDR messages.
                await adb.UpdateControls(settings, ct);
            } else if (effectiveTransport == "direct" && aoa != null) {
                var cmd = new {
                    command = "START_STREAM",
                    width = nextSettings.Width, height = nextSettings.Height,
                    fps = nextSettings.Fps, bitrate = nextSettings.BitrateMbps * 1_000_000,
                    focus = nextSettings.Focus, focusDistance = nextSettings.FocusDistance,
                    exposure = nextSettings.Exposure, wb = nextSettings.Wb,
                    powerMode = nextSettings.PowerMode, codec = nextSettings.Codec,
                    cameraKey = nextSettings.CameraKey, torch = nextSettings.Torch,
                    zoom = nextSettings.Zoom,
                    lockAeAwb = nextSettings.LockAeAwb,
                    faceTracking = nextSettings.FaceTracking,
                    autoFraming = nextSettings.AutoFraming,
                    autoFramingZoom = nextSettings.AutoFramingZoom,
                    autoFramingSpeed = nextSettings.AutoFramingSpeed,
                    autoFramingDeadzone = nextSettings.AutoFramingDeadzone,
                    manualIso = nextSettings.ManualIso,
                    shutterSpeedNs = nextSettings.ShutterSpeedNs,
                    manualWbKelvin = nextSettings.ManualWbKelvin,
                    stabilization = nextSettings.Stabilization,
                    stabilizationMode = nextSettings.StabilizationMode
                };
                var msg = H3HProtocol.Json(H3HMessageType.Command, 1, cmd, 0);
                await aoa.WriteAsync(msg, ct);
            }
        } catch (Exception ex) {
            log("Update controls: " + ex.Message);
        }
    }
    public async Task TapFocus(float x, float y, CancellationToken ct) {
        try {
            if (effectiveTransport == "wifi") {
                SendWifiControl(new { command = "TAP_FOCUS", x, y });
            } else if (effectiveTransport == "usb") {
                await adb.TapFocus(x, y, ct);
            } else if (effectiveTransport == "direct" && aoa != null) {
                var cmd = new { command = "TAP_FOCUS", x, y };
                var msg = H3HProtocol.Json(H3HMessageType.Command, 1, cmd, 0);
                await aoa.WriteAsync(msg, ct);
            }
        } catch (Exception ex) {
            log("Tap to focus: " + ex.Message);
        }
    }
    public int? RelayProcessId => ffmpeg?.Id;
    public long VirtualCameraFrames => virtualCamera?.Frames ?? 0;

    private void Track(Task task) {
        lock (taskLock) {
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(task);
        }
    }

    private async Task Guard(Func<Task> run) {
        try { await run(); }
        catch (OperationCanceledException) {}
        catch (ObjectDisposedException) {}
        catch (Exception ex) { if (lifetime?.IsCancellationRequested == false) log(ex.Message); }
    }

    public async Task Test(CancellationToken token) {
        UdpClient? udp = null;
        TcpListener? tcp = null;
        try {
            if (settings.Transport == "direct") {
                using var direct = new AoaController();
                var directResult = await direct.ConnectAsync(token);
                log(directResult.Status);
                if (!directResult.Ready) throw new IOException(directResult.Status);
                using var directTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                directTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                while (true) {
                    var message = await direct.ReadAsync(directTimeout.Token);
                    if (message?.Type == H3HMessageType.VideoFrame) break;
                }
                log("TEST OK · USB Direct · H3H protocol · H.264 frame");
                return;
            }
            await adb.Connect(token);
            var transport = adb.EffectiveTransport;
            settings.Validate(transport);
            settings.Save();

            if (transport == "wifi") {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, settings.RtpPort));
                udp.Client.ReceiveBufferSize = 256 * 1024;
            } else {
                tcp = new TcpListener(IPAddress.Loopback, settings.UsbPort);
                tcp.Start(1);
                await adb.Reverse(token);
            }

            await adb.StartStream(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            RtpProbeState result;
            try {
                result = transport == "wifi"
                    ? await ProbeWifi(udp!, timeout.Token)
                    : await ProbeUsb(tcp!, timeout.Token);
            } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                throw new TimeoutException("Поток не прошёл полную проверку за 7 секунд: нужны RTP, SPS/PPS, IDR и marker.");
            }
            var isHevc = settings.Codec == "hevc";
            log($"TEST OK ? {TransportLabel(transport)} ? {(isHevc ? "H.265 (HEVC)" : "H.264")} ? {result.Packets} RTP packets ? {(isHevc ? "VPS/SPS/PPS" : "SPS/PPS")} ? IDR ? marker ? SSRC {result.Ssrc:x8}");
        } finally {
            udp?.Dispose();
            tcp?.Stop();
            if (adb.Serial.Length > 0) {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await adb.Stop(cleanup.Token);
            }
        }
    }

    private async Task<RtpProbeState> ProbeWifi(UdpClient socket, CancellationToken ct) {
        var state = new RtpProbeState();
        var expected = IPAddress.Parse(Settings.EndpointHost(settings.PhoneIp));
        while (!state.Ready(settings.Codec)) {
            var datagram = await socket.ReceiveAsync(ct);
            if (!datagram.RemoteEndPoint.Address.Equals(expected)) continue;
            ObserveProbe(datagram.Buffer, state, settings.Codec);
        }
        return state;
    }

    private async Task<RtpProbeState> ProbeUsb(TcpListener server, CancellationToken ct) {
        var state = new RtpProbeState();
        var header = new byte[2];
        var packet = new byte[ushort.MaxValue];
        while (!state.Ready(settings.Codec)) {
            using var client = await server.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            var stream = client.GetStream();
            while (!state.Ready(settings.Codec) && await ReadExact(stream, header, 2, ct)) {
                var length = BinaryPrimitives.ReadUInt16BigEndian(header);
                if (length == 0) continue;
                if (!await ReadExact(stream, packet, length, ct)) break;
                ObserveProbe(packet.AsSpan(0, length), state, settings.Codec);
            }
        }
        return state;
    }

    private static void ObserveProbe(ReadOnlySpan<byte> packet, RtpProbeState state, string codec = "h264") {
        if (packet.Length < 12 || (packet[0] >> 6) != 2 || (packet[1] & 127) != 96) return;
        var offset = 12 + (packet[0] & 15) * 4;
        if (offset > packet.Length) return;
        if ((packet[0] & 16) != 0) {
            if (offset + 4 > packet.Length) return;
            var extensionWords = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
            offset += 4 + extensionWords * 4;
        }
        if (offset >= packet.Length) return;

        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        if (state.Ssrc != ssrc) state.StartSession(ssrc);
        state.Packets++;
        state.Marker |= (packet[1] & 128) != 0;

        if (codec == "hevc") {
            if (offset + 1 >= packet.Length) return;
            var hevcType = (packet[offset] >> 1) & 0x3F;
            if (hevcType == 32) state.Vps = true;
            else if (hevcType == 33) state.Sps = true;
            else if (hevcType == 34) state.Pps = true;
            else if (hevcType is 19 or 20 or 21) state.Idr = true;
            else if (hevcType == 49 && offset + 2 < packet.Length) {
                var fuType = packet[offset + 2] & 0x3F;
                if (fuType is 19 or 20 or 21) state.Idr = true;
            }
        } else {
            var type = packet[offset] & 31;
            if (type == 7) state.Sps = true;
            else if (type == 8) state.Pps = true;
            else if (type == 5) state.Idr = true;
            else if (type == 28 && offset + 1 < packet.Length && (packet[offset + 1] & 31) == 5)
                state.Idr = true;
        }
    }

    public async Task Start(CancellationToken token = default) {
        await lifecycle.WaitAsync(token);
        try {
            if (Running) return;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ct = lifetime.Token;
            if (settings.Transport is "usb" or "wifi")
                settings.AdbPath = ToolPaths.Find("adb.exe", settings.AdbPath);
            else if (settings.Transport == "auto") {
                try { settings.AdbPath = ToolPaths.Find("adb.exe", settings.AdbPath); }
                catch (FileNotFoundException) { log("ADB не найден; проверяем активный USB Direct."); }
            }
            settings.FfmpegPath = ToolPaths.Find("ffmpeg.exe", settings.FfmpegPath);
            if (settings.Preview) settings.FfplayPath = ToolPaths.Find("ffplay.exe", settings.FfplayPath);
            // ADB resolves the actual phone endpoint and local route into this runtime snapshot.
            adb.UpdateSettings(settings);

            if (settings.Transport == "direct") {
                effectiveTransport = "direct";
            } else if (settings.Transport == "auto") {
                effectiveTransport = await connectionManager.ResolveAutoTransportAsync(aoa, adb, settings, ct);
                if (effectiveTransport != "direct") {
                    await adb.Connect(ct);
                    effectiveTransport = adb.EffectiveTransport;
                    if (effectiveTransport == "usb") {
                        await adb.SetBatteryCharging(!settings.BatteryProtect, ct);
                    }
                }
            } else { 
                await adb.Connect(ct); 
                effectiveTransport = adb.EffectiveTransport;
                if (effectiveTransport == "usb") {
                    await adb.SetBatteryCharging(!settings.BatteryProtect, ct);
                }
            }
            connectionManager.OnConnected(effectiveTransport);
            RefreshExpectedPhoneAddress();
            settings.Validate(effectiveTransport);
            settings.Save();

            received = frames = packets = oldBytes = oldFrames = 0;
            lostPackets = recoveredPackets = nackRequests = oldLostPackets = oldRecoveredPackets = 0;
            adaptiveBitrate = settings.EffectiveBitrateMbps(effectiveTransport) * 1_000_000;
            stableSeconds = 0;
            modeSampleCount = 0; modeVerification = "проверка режима…";
            screenSlept = false;
            elapsed.Restart();
            selectedFps = settings.Fps;
            sourceGeneration = relayGeneration = 0;
            lastOutput = lastReceive = lastTelemetry = Environment.TickCount64;

            output = new UdpClient(AddressFamily.InterNetwork);
            output.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            output.Client.SendBufferSize = 4 * 1024 * 1024;
            forward = new UdpClient(AddressFamily.InterNetwork);
            forward.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            forward.Client.SendBufferSize = 4 * 1024 * 1024;
            ingestPort = FreeUdpPair();
            relayIngestEndpoint = new IPEndPoint(IPAddress.Loopback, ingestPort);
            Directory.CreateDirectory(Settings.DataDir);
            sdpPath = Path.Combine(Settings.DataDir, $"session-{Guid.NewGuid():N}.sdp");
            var rtpmap = settings.Codec == "hevc"
                ? "a=rtpmap:96 H265/90000\r\n"
                : "a=rtpmap:96 H264/90000\r\na=fmtp:96 packetization-mode=1\r\n";
            await File.WriteAllTextAsync(sdpPath,
                $"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=S8Cam\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=video {ingestPort} RTP/AVP 96\r\n{rtpmap}a=recvonly\r\n", ct);
            {
                do { previewIngestPort = FreeUdpPair(); }
                while (Math.Abs(previewIngestPort - ingestPort) < 2);
                previewIngestEndpoint = new IPEndPoint(IPAddress.Loopback, previewIngestPort);
                previewSdpPath = sdpPath + ".preview.sdp";
                var previewSdp = (await File.ReadAllTextAsync(sdpPath, ct))
                    .Replace($"m=video {ingestPort} ", $"m=video {previewIngestPort} ");
                await File.WriteAllTextAsync(previewSdpPath, previewSdp, ct);
            }
            {
                do { virtualIngestPort = FreeUdpPair(); }
                while (Math.Abs(virtualIngestPort - ingestPort) < 2 ||
                    Math.Abs(virtualIngestPort - previewIngestPort) < 2);
                virtualIngestEndpoint = new IPEndPoint(IPAddress.Loopback, virtualIngestPort);
                virtualSdpPath = sdpPath + ".camera.sdp";
                await File.WriteAllTextAsync(virtualSdpPath, (await File.ReadAllTextAsync(sdpPath, ct))
                    .Replace($"m=video {ingestPort} ", $"m=video {virtualIngestPort} "), ct);
                if (settings.VirtualCamera || settings.SpoutOutput) try {
                    virtualCamera = CreateVirtualCamera(ct);
                    virtualBootstrapped = false;
                    virtualGeneration = Volatile.Read(ref sourceGeneration);
                    if (settings.VirtualCamera) log("Камера для приложений: выберите OBS Virtual Camera. В OBS не включайте её выход одновременно с H3H Cam.");
                } catch (Exception ex) {
                    log("⚠️ Прямой видеовывод пропущен: " + ex.Message);
                    virtualCamera = null;
                }
            }

            await ConfigureInput(effectiveTransport, ct, initial: true);
            if (settings.Preview) StartPreview();
            if (settings.Obs) StartFfmpeg(ct);
            if (effectiveTransport != "direct") { StartLogcat(ct); await adb.StartStream(ct); }
            var effectiveBitrate = settings.EffectiveBitrateMbps(effectiveTransport);
            if (effectiveTransport == "wifi" && effectiveBitrate < settings.BitrateMbps)
                log($"Wi-Fi limit · bitrate reduced from {settings.BitrateMbps} to {effectiveBitrate} Mbps; FPS and low latency remain unchanged");
            Track(Guard(() => Monitor(ct)));
            var outLog = settings.Obs ? $"H.264 copy → MPEG-TS · OBS udp://127.0.0.1:{settings.ObsPort}" : (settings.SpoutOutput ? "Spout2 Direct Output" : "Virtual Camera Output");
            log($"START · {TransportLabel(effectiveTransport)} · {outLog}");
        } catch {
            await StopCore();
            throw;
        } finally {
            lifecycle.Release();
        }
    }

    private static string TransportLabel(string transport) =>
        transport == "direct" ? "USB DIRECT · AOA" : transport == "usb" ? "USB · RTP/TCP" : "WI-FI · RTP/UDP";

    private void RefreshExpectedPhoneAddress() {
        expectedPhoneAddress = IPAddress.TryParse(Settings.EndpointHost(settings.PhoneIp), out var address)
            ? address
            : null;
    }

    private static int FreeUdpPair() {
        for (var i = 0; i < 100; i++) {
            // RTP uses an even port followed by RTCP; avoid Windows' dynamic port range.
            var port = Random.Shared.Next(10000, 24000) * 2;
            try {
                using var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                using var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                return port;
            } catch (SocketException) {}
        }
        throw new IOException("Нет свободной пары RTP/RTCP портов");
    }

    private async Task ConfigureInput(string transport, CancellationToken ct, bool initial = false) {
        if (transport is not ("wifi" or "usb" or "direct")) throw new ArgumentException("Неизвестный transport: " + transport);

        if (!initial && effectiveTransport == transport && inputTask?.IsCompleted == false) {
            if (transport == "usb") await adb.Reverse(ct);
            return;
        }

        var oldCts = inputLifetime;
        var oldTask = inputTask;
        oldCts?.Cancel();
        incoming?.Dispose();
        control?.Dispose(); control = null; controlEndpoint = null;
        listener?.Stop();
        usbClient?.Dispose();
        aoa?.Dispose(); aoa = null;
        if (oldTask != null) {
            try { await oldTask.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (TimeoutException) { log("Transport cleanup timeout"); }
        }
        oldCts?.Dispose();

        incoming = null;
        listener = null;
        usbClient = null;
        inputTask = null;
        effectiveTransport = transport;
        inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inputToken = inputLifetime.Token;

        if (transport == "direct") {
            aoa = new AoaController();
            var result = await aoa.ConnectAsync(ct);
            log(result.Status);
            if (!result.Ready) throw new IOException(result.Status);
            await aoa.WriteAsync(H3HProtocol.Json(H3HMessageType.Command, 1, new {
                command = "START_STREAM", cameraKey = settings.CameraKey, width = settings.Width,
                height = settings.Height, fps = settings.Fps, bitrate = settings.BitrateMbps * 1_000_000,
                focus = settings.Focus, focusDistance = settings.FocusDistance,
                exposure = settings.Exposure, wb = settings.Wb, powerMode = settings.PowerMode,
                codec = settings.Codec, torch = settings.Torch, zoom = settings.Zoom,
                lockAeAwb = settings.LockAeAwb, faceTracking = settings.FaceTracking,
                autoFraming = settings.AutoFraming, autoFramingZoom = settings.AutoFramingZoom,
                autoFramingSpeed = settings.AutoFramingSpeed, autoFramingDeadzone = settings.AutoFramingDeadzone,
                manualIso = settings.ManualIso, shutterSpeedNs = settings.ShutterSpeedNs,
                manualWbKelvin = settings.ManualWbKelvin,
                stabilization = settings.Stabilization,
                stabilizationMode = settings.StabilizationMode
            }), ct);
            inputTask = Guard(() => ReceiveDirect(aoa, inputToken));
        } else if (transport == "wifi") {
            var socket = new UdpClient(new IPEndPoint(IPAddress.Any, settings.RtpPort));
            socket.Client.ReceiveBufferSize = 4 * 1024 * 1024;
            incoming = socket;
            control = new UdpClient(AddressFamily.InterNetwork);
            // Match the LAN source address advertised to Android (also on PCs with VPN/tethering).
            control.Client.Bind(new IPEndPoint(IPAddress.Parse(settings.PcIp), 0));
            if (expectedPhoneAddress != null)
                controlEndpoint = new IPEndPoint(expectedPhoneAddress, settings.RtpPort + 1);
            inputTask = Guard(() => ReceiveRtp(socket, inputToken));
        } else {
            var server = new TcpListener(IPAddress.Loopback, settings.UsbPort);
            server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            server.Start(1);
            listener = server;
            await adb.Reverse(ct);
            inputTask = Guard(() => ReceiveUsb(server, inputToken));
        }
        Track(inputTask);

        if (!initial) {
            Interlocked.Increment(ref sourceGeneration);
            log($"Transport switched · {TransportLabel(transport)}");
            if (ffmpeg != null) await RestartFfmpeg(ct);
        }
    }

    private void StartFfmpeg(CancellationToken ct) {
        relayBootstrapped = false;
        relayGeneration = Volatile.Read(ref sourceGeneration);
        var args = MediaCommands.Relay(settings, sdpPath, Volatile.Read(ref selectedFps));
        var p = Processes.Start(settings.FfmpegPath, args, s => log("FFmpeg · " + s), stdout: true, videoPriority: true);
        ffmpeg = p;
        Track(Guard(() => FanOut(p, ct)));
    }

    private async Task RestartFfmpeg(CancellationToken ct) {
        var old = ffmpeg;
        Processes.Kill(old);
        if (old != null) {
            try { await old.WaitForExitAsync(ct); } catch (InvalidOperationException) {}
            old.Dispose();
        }
        StartFfmpeg(ct);
        Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
    }

    private void StartPreview() {
        virtualCamera?.SetPreviewEnabled(settings.Preview && previewActive);
        if (SharedPreview) {
            if (previewDecoder != null) {
                _ = previewDecoder.DisposeAsync();
                previewDecoder = null;
            }
            previewGeneration = Volatile.Read(ref sourceGeneration);
            return;
        }
        previewBootstrapped = false;
        previewGeneration = Volatile.Read(ref sourceGeneration);
        if (previewDecoder == null && File.Exists(previewSdpPath)) {
            try {
                previewDecoder = new PreviewDecoder(settings, previewSdpPath, log);
                previewDecoder.FrameReady += (buf, w, h) => PreviewFrame?.Invoke(buf, w, h);
            } catch (Exception ex) {
                log("PreviewDecoder error: " + ex.Message);
            }
        }
    }

    private void StopPreview() {
        virtualCamera?.SetPreviewEnabled(false);
        if (previewDecoder != null) {
            _ = previewDecoder.DisposeAsync();
            previewDecoder = null;
        }
        if (ffplay != null) {
            Processes.Kill(ffplay);
            ffplay?.Dispose();
            ffplay = null;
        }
    }

    public void LaunchExternalFfplay() {
        if (!File.Exists(previewSdpPath)) return;
        try {
            settings.FfplayPath = ToolPaths.Find("ffplay.exe", settings.FfplayPath);
            var outDim = settings.OutputDimensions;
            var vf = settings.BuildVideoFilter(outDim.Width, outDim.Height);
            var args = new List<string> {
                "-hide_banner", "-loglevel", "warning", "-window_title", "H3H Cam • External Preview",
                "-x", "960", "-y", "540",
                "-fflags", "nobuffer", "-flags", "low_delay", "-threads", "1", "-framedrop", "-noinfbuf",
                "-analyzeduration", "100000", "-probesize", "100000", "-sync", "ext",
                "-protocol_whitelist", "file,udp,rtp", "-buffer_size", "2097152", "-max_delay", "30000", "-reorder_queue_size", "64"
            };
            if (!string.IsNullOrEmpty(vf)) {
                args.Add("-vf");
                args.Add(vf);
            }
            args.Add("-i");
            args.Add(previewSdpPath);
            ffplay = Processes.Start(settings.FfplayPath, args, s => log("External Preview · " + s), videoPriority: true);
        } catch (Exception ex) {
            log("Launch ffplay: " + ex.Message);
        }
    }

    private async Task ReceiveRtp(UdpClient socket, CancellationToken ct) {
        var state = new RtpSessionState();
        while (!ct.IsCancellationRequested) {
            var datagram = await socket.ReceiveAsync(ct);
            var expected = expectedPhoneAddress;
            if (expected == null || !datagram.RemoteEndPoint.Address.Equals(expected)) {
                if (datagram.Buffer.Length >= 12 && (datagram.Buffer[0] & 0xC0) == 0x80) {
                    expectedPhoneAddress = datagram.RemoteEndPoint.Address;
                    controlEndpoint = new IPEndPoint(datagram.RemoteEndPoint.Address, settings.RtpPort + 1);
                    expected = expectedPhoneAddress;
                } else {
                    continue;
                }
            }
            await ForwardRtp(datagram.Buffer, datagram.Buffer.Length, state, ct);
        }
    }

    private async Task ReceiveUsb(TcpListener server, CancellationToken ct) {
        var state = new RtpSessionState();
        var header = new byte[2];
        var packet = new byte[ushort.MaxValue];
        while (!ct.IsCancellationRequested) {
            using var client = await server.AcceptTcpClientAsync(ct);
            usbClient = client;
            client.NoDelay = true;
            client.ReceiveBufferSize = 4 * 1024 * 1024;
            log("USB RTP/TCP connected");
            try {
                var stream = client.GetStream();
                while (!ct.IsCancellationRequested) {
                    if (!await ReadExact(stream, header, 2, ct)) break;
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header);
                    if (length == 0) continue; // RFC 4571 null frame.
                    if (!await ReadExact(stream, packet, length, ct))
                        throw new EndOfStreamException("USB RFC4571 frame truncated");
                    await ForwardRtp(packet, length, state, ct);
                }
            } catch (Exception ex) when (!ct.IsCancellationRequested) {
                log("USB RTP reconnect · " + ex.Message);
            } finally {
                usbClient = null;
            }
        }
    }

    private async Task ReceiveDirect(AoaController direct, CancellationToken ct) {
        var state = new RtpSessionState();
        var packetizer = new RtpH264Packetizer(settings.Codec);
        while (!ct.IsCancellationRequested) {
            var message = await direct.ReadAsync(ct) ?? throw new EndOfStreamException("USB Direct disconnected");
            if (message.Type == H3HMessageType.Telemetry) {
                try {
                    using var doc = JsonDocument.Parse(message.Payload);
                    var j = doc.RootElement;
                    androidState = StringValue(j, "state", androidState);
                    encodedMbps = DoubleValue(j, "bitrateMbps", encodedMbps);
                    resolution = StringValue(j, "resolution", resolution);
                    var actual = IntValue(j, "selectedFps", 0); if (actual > 0) selectedFps = actual;
                    var dropped = IntValue(j, "dropped", 0);
                    Volatile.Write(ref droppedFrames, dropped);
                    var dropStr = dropped > 0 ? $" · drop {dropped}" : "";
                    details = StringValue(j, "codec", "—") + dropStr + " · " + StringValue(j, "controls", "");
                    thermal = BatterySummary(j);
                    power = PowerTelemetry.From(j);
                    Interlocked.Exchange(ref lastTelemetry, Environment.TickCount64);
                } catch (JsonException) { }
                continue;
            }
            if (message.Type != H3HMessageType.VideoFrame) continue;
            foreach (var packet in packetizer.Packetize(message.Payload, message.TimestampUs))
                await ForwardRtp(packet, packet.Length, state, ct);
        }
    }

    private static async Task<bool> ReadExact(NetworkStream stream, byte[] buffer, int length, CancellationToken ct) {
        var offset = 0;
        while (offset < length) {
            var count = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct);
            if (count == 0) return false;
            offset += count;
        }
        return true;
    }

    private async Task ForwardRtp(byte[] packet, int length, RtpSessionState state, CancellationToken ct) {
        if (!ObserveRtp(packet.AsSpan(0, length), state)) return;
        Interlocked.Add(ref received, length);
        Interlocked.Increment(ref packets);
        if ((packet[1] & 128) != 0) Interlocked.Increment(ref frames);
        Interlocked.Exchange(ref lastReceive, Environment.TickCount64);
        // Pause every sink, including copy-only MPEG-TS/recording which bypass video filters.
        // Keep ingest statistics alive so a privacy pause does not trigger transport recovery.
        if (settings.PrivacyMute) return;
        // Preview receives the original RTP without waiting for OBS remuxing.
        var isHevc = settings.Codec == "hevc";
        var bootstrap = isHevc
            ? length > 13 && (((packet[12] >> 1) & 0x3F) is 32 or 33)
            : length > 12 && (packet[12] & 31) == 7;
        var recordTarget = recordingEndpoint;
        if (recordTarget != null) {
            var generation = Volatile.Read(ref sourceGeneration);
            if (recordingGeneration != generation) {
                recordingBootstrapped = false;
                recordingGeneration = generation;
            }
            if (recordingBootstrapped || bootstrap) {
                recordingBootstrapped = true;
                await forward!.SendAsync(packet.AsMemory(0, length), recordTarget, ct);
            }
        }
        if (settings.Preview && (previewDecoder != null || ffplay?.HasExited == false) && previewGeneration == Volatile.Read(ref sourceGeneration) &&
            (previewBootstrapped || bootstrap)) {
            previewBootstrapped = true;
            await forward!.SendAsync(packet.AsMemory(0, length), previewIngestEndpoint!, ct);
        }
        if ((settings.VirtualCamera || settings.SpoutOutput) && virtualCamera != null && virtualGeneration == Volatile.Read(ref sourceGeneration) &&
            (virtualBootstrapped || bootstrap)) {
            virtualBootstrapped = true;
            await forward!.SendAsync(packet.AsMemory(0, length), virtualIngestEndpoint!, ct);
        }
        if (settings.Obs && Volatile.Read(ref sourceGeneration) == Volatile.Read(ref relayGeneration) && (relayBootstrapped || bootstrap)) {
            relayBootstrapped = true;
            await forward!.SendAsync(packet.AsMemory(0, length), relayIngestEndpoint!, ct);
        }
    }

    private bool ObserveRtp(ReadOnlySpan<byte> packet, RtpSessionState state) {
        if (packet.Length < 12 || (packet[0] >> 6) != 2 || (packet[1] & 127) != 96) return false;
        var nextSsrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        var nextTimestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        var delta = state.Timestamp.HasValue ? unchecked((int)(nextTimestamp - state.Timestamp.Value)) : 0;
        if (state.Ssrc.HasValue && (state.Ssrc.Value != nextSsrc || delta > 450000 || delta < -90000)) {
            Interlocked.Increment(ref sourceGeneration);
            log("RTP: новая сессия или разрыв временной шкалы; пересоздание приёмника");
            state.ExpectedSequence = null;
            state.Missing.Clear();
            rtpTransitMinOffset = double.PositiveInfinity;
            estimatedLatencyMs = 0;
            RequestIdr();
        }
        state.Ssrc = nextSsrc;
        state.Timestamp = nextTimestamp;
        var localMs = elapsed.Elapsed.TotalMilliseconds;
        var rtpMs = nextTimestamp / 90.0;
        var offset = localMs - rtpMs;
        var nowTick = Environment.TickCount64;
        if (offset < rtpTransitMinOffset || nowTick - lastOffsetReset > 30000) {
            rtpTransitMinOffset = offset;
            lastOffsetReset = nowTick;
        }
        var jitterDelay = Math.Max(0.0, offset - rtpTransitMinOffset);
        var sampleLatency = Math.Clamp(jitterDelay, 0.0, 300.0);
        estimatedLatencyMs = estimatedLatencyMs * 0.90 + sampleLatency * 0.10;
        if (state.ExpectedSequence is ushort expected) {
            var distance = (sequence - expected) & 0xffff;
            if (distance == 0) state.ExpectedSequence = unchecked((ushort)(sequence + 1));
            else if (distance < 0x8000) {
                var count = Math.Min(distance, 64);
                var missing = new ushort[count];
                for (var i = 0; i < count; i++) {
                    missing[i] = unchecked((ushort)(expected + i));
                    state.Missing.Add(missing[i]);
                }
                Interlocked.Add(ref lostPackets, distance);
                Interlocked.Increment(ref nackRequests);
                SendWifiControl(new { command = "NACK", sequences = missing });
                state.ExpectedSequence = unchecked((ushort)(sequence + 1));
                state.Missing.RemoveWhere(missingSequence => ((sequence - missingSequence) & 0xffff) > 768);
                if (distance > 20) {
                    RequestIdr();
                }
            } else if (state.Missing.Remove(sequence)) {
                Interlocked.Increment(ref recoveredPackets);
            } else return false; // discard duplicates and expired retransmissions
        } else state.ExpectedSequence = unchecked((ushort)(sequence + 1));
        return true;
    }

    public void RequestIdr() {
        var now = Environment.TickCount64;
        if (now - lastIdrRequestTick < 1000) return; // rate limit: max 1 per second
        lastIdrRequestTick = now;
        try {
            if (effectiveTransport == "wifi") {
                SendWifiControl(new { command = "REQUEST_IDR" });
            } else if (effectiveTransport == "usb") {
                _ = Task.Run(() => adb.RequestIdr(CancellationToken.None));
            } else if (effectiveTransport == "direct" && aoa != null) {
                _ = aoa.WriteAsync(H3HProtocol.Json(H3HMessageType.Command, 1, new { command = "REQUEST_IDR" }, 0), CancellationToken.None);
            }
        } catch { }
    }

    private void SendWifiControl(object command) {
        var socket = control;
        var endpoint = controlEndpoint;
        if (effectiveTransport != "wifi" || socket == null || endpoint == null) return;
        try {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
            _ = socket.SendAsync(bytes, endpoint);
        } catch (Exception ex) { log("Wi-Fi control · " + ex.Message); }
    }

    private async Task FanOut(Process process, CancellationToken ct) {
        // MPEG-TS packets stay aligned at 188 bytes; 7 packets fit one 1316-byte UDP payload.
        var buffer = new byte[1316];
        var count = 0;
        while (!ct.IsCancellationRequested) {
            var n = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(count), ct);
            if (n == 0) break;
            count += n;
            var complete = count / 188 * 188;
            if (complete == 0) continue;
            Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
            if (settings.Obs && !settings.PrivacyMute)
                await output!.SendAsync(buffer.AsMemory(0, complete), new IPEndPoint(IPAddress.Loopback, settings.ObsPort), ct);
            Buffer.BlockCopy(buffer, complete, buffer, 0, count - complete);
            count -= complete;
        }
    }

    private void StartLogcat(CancellationToken ct) {
        Processes.Kill(logcat);
        logcat?.Dispose();
        logcatSerial = adb.Serial;
        lastLogcatStart = Environment.TickCount64;
        logcat = Processes.Start(settings.AdbPath,
            ["-s", logcatSerial, "logcat", "-v", "raw", "-T", "1", "S8CamStats:I", "*:S"], null, stdout: true);
        var process = logcat;
        Track(Guard(async () => {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line) {
                if (!line.StartsWith('{')) continue;
                try {
                    using var doc = JsonDocument.Parse(line);
                    var j = doc.RootElement;
                    if (TryString(j, "sessionId", out var sessionId) &&
                        !string.IsNullOrEmpty(adb.SessionId) && sessionId != adb.SessionId) continue;

                    androidState = StringValue(j, "state", androidState);
                    encodedMbps = DoubleValue(j, "bitrateMbps", encodedMbps);
                    var cameraFps = IntValue(j, "selectedFps", 0);
                    if (cameraFps is >= 1 and <= 240) Volatile.Write(ref selectedFps, cameraFps);
                    var codec = StringValue(j, "codec", "—");
                    var controls = StringValue(j, "controls", "");
                    var camera = StringValue(j, "camera", "");
                    var dropped = IntValue(j, "dropped", 0);
                    Volatile.Write(ref droppedFrames, dropped);
                    var dropStr = dropped > 0 ? $" · drop {dropped}" : "";
                    details = codec + " · encoder " + DoubleValue(j, "fps", 0).ToString("F1", CultureInfo.InvariantCulture) + " fps" +
                        (camera.Length == 0 ? "" : " · " + camera) + dropStr + (controls.Length == 0 ? "" : " · " + controls);
                    resolution = StringValue(j, "resolution", resolution);
                    thermal = BatterySummary(j);
                    power = PowerTelemetry.From(j);
                    if (j.TryGetProperty("thermalStatus", out var th) && th.ValueKind == JsonValueKind.Number)
                        thermal += " · thermal " + th;
                    if (j.TryGetProperty("faceX", out var fx) && fx.ValueKind == JsonValueKind.Number) {
                        var faceX = (float)fx.GetDouble();
                        var faceY = (float)DoubleValue(j, "faceY", 0.5);
                        var faceW = (float)DoubleValue(j, "faceW", 0.0);
                        var faceH = (float)DoubleValue(j, "faceH", 0.0);
                        var cropCx = (float)DoubleValue(j, "cropCx", 0.5);
                        var cropCy = (float)DoubleValue(j, "cropCy", 0.5);
                        var cropZoom = (float)DoubleValue(j, "cropZoom", 1.0);
                        var faceCount = j.TryGetProperty("faceCount", out var fc) && fc.ValueKind == JsonValueKind.Number ? fc.GetInt32() : 1;
                        lastFace = new FaceTrackingInfo(faceX, faceY, faceW, faceH, cropCx, cropCy, cropZoom, faceCount);
                    }
                    Interlocked.Exchange(ref lastTelemetry, Environment.TickCount64);
                } catch (JsonException) {}
            }
        }));
    }

    private static bool TryString(JsonElement json, string name, out string value) {
        value = "";
        if (!json.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString() ?? "";
        return true;
    }

    private static string StringValue(JsonElement json, string name, string fallback) =>
        TryString(json, name, out var value) ? value : fallback;

    private static double DoubleValue(JsonElement json, string name, double fallback) =>
        json.TryGetProperty(name, out var item) && item.TryGetDouble(out var value) ? value : fallback;

    private static int IntValue(JsonElement json, string name, int fallback) =>
        json.TryGetProperty(name, out var item) && item.TryGetInt32(out var value) ? value : fallback;

    private static string BatterySummary(JsonElement json) {
        return PowerTelemetry.From(json).Compact;
    }

    private async Task Monitor(CancellationToken ct) {
        var previous = elapsed.Elapsed.TotalSeconds;
        var lastRecovery = 0L;
        long previousOutputFrames = VirtualCameraFrames;
        while (true) {
            await Task.Delay(1000, ct);
            var now = elapsed.Elapsed.TotalSeconds;
            var duration = now - previous;
            previous = now;
            var bytes = Interlocked.Read(ref received);
            var frameCount = Interlocked.Read(ref frames);
            var rate = (bytes - oldBytes) * 8 / duration / 1e6;
            var fps = (frameCount - oldFrames) / duration;
            oldBytes = bytes;
            oldFrames = frameCount;
            var tick = Environment.TickCount64;
            var idle = tick - Interlocked.Read(ref lastReceive);
            var telemetryAge = tick - Interlocked.Read(ref lastTelemetry);
            var lost = Interlocked.Read(ref lostPackets);
            var recovered = Interlocked.Read(ref recoveredPackets);
            var newLost = lost - oldLostPackets;
            var newRecovered = recovered - oldRecoveredPackets;
            oldLostPackets = lost; oldRecoveredPackets = recovered;
            if (effectiveTransport == "wifi" && settings.AdaptiveBitrate) {
                var ceiling = settings.EffectiveBitrateMbps("wifi") * 1_000_000;
                var floor = Math.Min(settings.MinimumWifiBitrateMbps * 1_000_000, ceiling);
                var unrecoveredLoss = newLost - newRecovered;
                if (unrecoveredLoss >= 5 && adaptiveBitrate > floor) {
                    var next = Math.Max(floor, adaptiveBitrate - 2_000_000);
                    if (next < adaptiveBitrate) {
                        adaptiveBitrate = next; stableSeconds = 0;
                        SendWifiControl(new { command = "SET_BITRATE", bitrate = next });
                        log($"Adaptive Wi-Fi · RTP loss {newLost}, recovered {newRecovered}; bitrate {next / 1_000_000} Mbps");
                    }
                } else if (unrecoveredLoss <= 0 && rate > 0 && adaptiveBitrate < ceiling) {
                    if (++stableSeconds >= 2) {
                        adaptiveBitrate = Math.Min(ceiling, adaptiveBitrate + 3_000_000);
                        stableSeconds = 0;
                        SendWifiControl(new { command = "SET_BITRATE", bitrate = adaptiveBitrate });
                        log($"Adaptive Wi-Fi · stable; bitrate {adaptiveBitrate / 1_000_000} Mbps");
                    }
                } else if (unrecoveredLoss > 0) stableSeconds = 0;
            }
            if (effectiveTransport == "wifi" && estimatedLatencyMs > 200 && idle < 1500 && tick - lastIdrRequestTick > 4000) {
                RequestIdr();
                log($"Latency drift mitigation · estimated latency {estimatedLatencyMs:F0} ms; requested IDR keyframe");
            }
            if (fps > 0 && elapsed.Elapsed.TotalSeconds >= 4 && modeSampleCount < 4) {
                modeSampleCount++;
                if (modeSampleCount == 4) {
                    var ratio = fps / Math.Max(1, settings.Fps);
                    modeVerification = ratio >= 0.90
                        ? $"режим проверен: {resolution} {settings.Fps} FPS"
                        : $"режим не держит {settings.Fps} FPS: фактически {fps:F1}";
                    log("Camera mode · " + modeVerification);
                }
            }
            var outputFrames = VirtualCameraFrames;
            var outputFps = Math.Max(0, outputFrames - previousOutputFrames) / duration;
            previousOutputFrames = outputFrames;
            var statusDetails = details + (telemetryAge > 5000 ? " · telemetry reconnecting" : "") +
                " · " + modeVerification +
                (effectiveTransport == "wifi" ? $" · RTP loss {lost}, recovered {recovered}, NACK {Interlocked.Read(ref nackRequests)} · target {adaptiveBitrate / 1e6:F0} Mbps" : "") +
                ((settings.VirtualCamera || settings.SpoutOutput) ? $" · Direct/Spout: {outputFps:F1} FPS · {outputFrames} кадров · пропущено устаревших {virtualCamera?.SkippedFrames ?? 0} · GPU skipped {virtualCamera?.GpuDroppedFrames ?? 0}" : "");
            Status?.Invoke(new LiveStatus(settings.PrivacyMute ? "Приватность · передача приостановлена" : idle > 4000 ? "Ожидание потока / reconnect" : androidState,
                effectiveTransport == "direct" ? "USB accessory" : adb.Serial, TransportLabel(effectiveTransport), resolution, settings.Fps, fps, rate, encodedMbps,
                Interlocked.Read(ref packets), elapsed.Elapsed,
                !settings.Obs ? "прямой вывод камеры · relay выключен" : ffmpeg?.HasExited == false ? "running · RTP copy" : "restarting", thermal, statusDetails, power,
                lost, recovered, Volatile.Read(ref droppedFrames), estimatedLatencyMs, lastFace, connectionManager.State));

            try {
                if (effectiveTransport == "usb") {
                    await adb.SetBatteryCharging(!settings.BatteryProtect, ct);
                }
                // Do not race a user update or wait behind STOP while it awaits this monitor.
                if (await lifecycle.WaitAsync(0, ct)) {
                  try {
                   if ((settings.VirtualCamera || settings.SpoutOutput) && (virtualCamera?.Alive != true ||
                    virtualGeneration != Volatile.Read(ref sourceGeneration) || (fps>5 && virtualCamera?.Stalled==true))) {
                    if(virtualCamera?.Stalled==true)log("Видеовывод не публикует кадры; перезапуск декодера с новым IDR.");
                    var oldCamera = virtualCamera;
                    virtualCamera = null;
                    if (oldCamera != null) await oldCamera.DisposeAsync();
                    virtualBootstrapped = false;
                    try {
                        virtualCamera = CreateVirtualCamera(ct);
                        virtualGeneration = Volatile.Read(ref sourceGeneration);
                    } catch (Exception ex) {
                        log("⚠️ Прямой видеовывод недоступен: " + ex.Message);
                        virtualCamera = null;
                    }
                    RequestIdr();
                   }
                  } finally { lifecycle.Release(); }
                }
                if (settings.ScreenOff && effectiveTransport != "direct" && !screenSlept && fps > 0) {
                    await adb.Command(ct, "shell", "input", "keyevent", "223");
                    screenSlept = true;
                    log("Экран телефона погашен; видеопередача продолжается.");
                }
                if (settings.Obs && !settings.PrivacyMute && (ffmpeg?.HasExited != false ||
                    Volatile.Read(ref sourceGeneration) != Volatile.Read(ref relayGeneration) ||
                    (rate > 0 && tick - Interlocked.Read(ref lastOutput) > 6000))) {
                    log("FFmpeg exited, RTP session changed, or output stalled; restart");
                    await RestartFfmpeg(ct);
                }
                if (!settings.Preview && (previewDecoder != null || ffplay != null)) {
                    StopPreview();
                    log("Предпросмотр выключен; передача продолжается.");
                } else if (settings.Preview && ((previewDecoder?.Alive != true && !SharedPreview) || (previewDecoder != null && SharedPreview) || previewGeneration != Volatile.Read(ref sourceGeneration))) {
                    StopPreview();
                    try { StartPreview(); }
                    catch {
                        settings.Preview = false;
                        PreviewChanged?.Invoke(false);
                    }
                }
                if (effectiveTransport != "direct" && (logcat?.HasExited != false || logcatSerial != adb.Serial) && tick - lastLogcatStart > 5000)
                    StartLogcat(ct);
            } catch (Exception ex) when (!ct.IsCancellationRequested) {
                log("Monitor · " + ex.Message);
            }

            var isUsbOrDirect = effectiveTransport is "usb" or "direct";
            var stallTimeout = (settings.AutoReconnect && isUsbOrDirect) ? 3500 : 8000;
            var retryInterval = (settings.AutoReconnect && isUsbOrDirect) ? 4000 : 10000;
            if (idle > stallTimeout && tick - lastRecovery > retryInterval) {
                lastRecovery = tick;
                recoveryAttempt++;
                var backoff = ConnectionManager.CalculateBackoffMs(recoveryAttempt);
                connectionManager.OnRecovering(effectiveTransport, recoveryAttempt, backoff);
                try {
                    var previousTransport = effectiveTransport;
                    if (settings.Transport == "auto") {
                        var nextTransport = await connectionManager.ResolveAutoTransportAsync(aoa, adb, settings, ct);
                        if (nextTransport == "direct") {
                            await ConfigureInput("direct", ct);
                            recoveryAttempt = 0;
                            connectionManager.OnConnected("direct");
                        } else if (nextTransport == "usb") {
                            var ok = await adb.TryReconnectUsb(ct, maxRetries: 5, delayMs: 800);
                            if (ok) {
                                RefreshExpectedPhoneAddress();
                                await ConfigureInput("usb", ct);
                                await adb.StartStream(ct);
                                screenSlept = false;
                                Interlocked.Exchange(ref lastReceive, Environment.TickCount64);
                                recoveryAttempt = 0;
                                connectionManager.OnConnected("usb");
                            }
                        } else {
                            await adb.Connect(ct);
                            RefreshExpectedPhoneAddress();
                            await ConfigureInput(adb.EffectiveTransport, ct);
                            await adb.StartStream(ct);
                            screenSlept = false;
                            recoveryAttempt = 0;
                            connectionManager.OnConnected("wifi");
                        }
                    } else if (effectiveTransport == "direct") {
                        await ConfigureInput("direct", ct);
                        recoveryAttempt = 0;
                        connectionManager.OnConnected("direct");
                    } else if (effectiveTransport == "usb" && settings.AutoReconnect) {
                        log("USB поток прерван. Запуск сторожевого пса авто-переподключения...");
                        var ok = await adb.TryReconnectUsb(ct, maxRetries: 10, delayMs: 800);
                        if (ok) {
                            RefreshExpectedPhoneAddress();
                            await ConfigureInput("usb", ct);
                            await adb.StartStream(ct);
                            screenSlept = false;
                            Interlocked.Exchange(ref lastReceive, Environment.TickCount64);
                            recoveryAttempt = 0;
                            connectionManager.OnConnected("usb");
                            log("USB поток успешно восстановлен авто-переподключением.");
                        }
                    } else {
                        await adb.Connect(ct);
                        RefreshExpectedPhoneAddress();
                        await ConfigureInput(adb.EffectiveTransport, ct);
                        await adb.StartStream(ct);
                        screenSlept = false;
                        recoveryAttempt = 0;
                        connectionManager.OnConnected(effectiveTransport);
                    }
                    if (effectiveTransport != "direct" && (logcat?.HasExited != false || logcatSerial != adb.Serial || previousTransport != effectiveTransport))
                        StartLogcat(ct);
                } catch (Exception ex) when (!ct.IsCancellationRequested) {
                    log("Reconnect · " + ex.Message);
                }
            }
        }
    }

    public async Task Stop() {
        await lifecycle.WaitAsync();
        try { await StopCore(); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCore() {
        var cts = lifetime;
        if (cts == null) return;
        // FFmpeg must still receive input while it processes its quit command.
        StopRecording();
        cts.Cancel();
        inputLifetime?.Cancel();
        incoming?.Dispose();
        control?.Dispose();
        forward?.Dispose();
        listener?.Stop();
        usbClient?.Dispose();
        aoa?.Dispose();
        Processes.Kill(ffmpeg);
        Processes.Kill(ffplay);
        Processes.Kill(logcat);

        Task[] pending;
        lock (taskLock) {
            pending = tasks.ToArray();
            tasks.Clear();
        }
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { log("Cleanup · " + ex.Message); }

        if (virtualCamera != null) {
            await virtualCamera.DisposeAsync();
            virtualCamera = null;
        }
        if (previewDecoder != null) {
            await previewDecoder.DisposeAsync();
            previewDecoder = null;
        }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await adb.Stop(cleanup.Token);
        inputLifetime?.Dispose();
        output?.Dispose();
        ffmpeg?.Dispose();
        ffplay?.Dispose();
        logcat?.Dispose();
        if (sdpPath.Length > 0) {
            try { File.Delete(sdpPath); } catch (IOException) {}
        }
        if (previewSdpPath.Length > 0) {
            try { File.Delete(previewSdpPath); } catch (IOException) {}
            previewSdpPath = "";
        }
        if (virtualSdpPath.Length > 0) {
            try { File.Delete(virtualSdpPath); } catch (IOException) {}
            virtualSdpPath = "";
        }

        incoming = forward = output = null;
        listener = null;
        usbClient = null;
        aoa = null;
        inputLifetime = null;
        inputTask = null;
        ffmpeg = ffplay = logcat = null;
        elapsed.Stop();
        lifetime = null;
        cts.Dispose();
        connectionManager.OnDisconnected("Остановлено");
        log("STOP · локальные процессы и сокеты закрыты");
    }

    public async ValueTask DisposeAsync() {
        await Stop();
        lifecycle.Dispose();
    }
}
