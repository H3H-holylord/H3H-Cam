using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace S8Cam;

public sealed class AdbController(Settings initialSettings, Action<string> log) {
    private Settings settings = initialSettings;
    internal void UpdateSettings(Settings next) => settings = next;
    private const string PackageName = "com.h3h.s8cam";
    private const string ControlActivity = PackageName + "/.ControlActivity";
    private const string StreamService = PackageName + "/.StreamService";
    private const string CapabilitiesUri = "content://com.h3h.s8cam.capabilities";

    private List<AdbDevice> devices = [];
    private bool ownReverse;
    private string reverseSerial = "";
    private bool? lastBatteryProtectState;

    public string Serial { get; private set; } = "";
    public string EffectiveTransport { get; private set; } = "";
    public string SessionId { get; private set; } = "";
    public AdbDevice? Device { get; private set; }
    public IReadOnlyList<AdbDevice> Devices => devices;

    private string Adb => ToolPaths.Find("adb.exe", settings.AdbPath);

    public Task<string> Command(CancellationToken ct, params string[] args) {
        if (string.IsNullOrWhiteSpace(Serial))
            throw new InvalidOperationException(L.Get("s_c896ce04b1e5"));
        return Processes.Run(Adb, new[] { "-s", Serial }.Concat(args), ct);
    }

    private Task<string> HostCommand(CancellationToken ct, params string[] args) =>
        Processes.Run(Adb, args, ct);

    public async Task<IReadOnlyList<AdbDevice>> RefreshDevices(CancellationToken ct) {
        await HostCommand(ct, "start-server");
        devices = ParseDevices(await HostCommand(ct, "devices", "-l")).ToList();
        return devices;
    }

    public static async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(string adbPath, CancellationToken ct) {
        var adb = ToolPaths.Find("adb.exe", adbPath);
        var output = await Processes.Run(adb, ["devices", "-l"], ct);
        return ParseDevices(output);
    }

    public async Task<bool> TryReconnectUsb(CancellationToken ct, int maxRetries = 12, int delayMs = 1000) {
        log(L.Get("s_a4cc7881635b"));
        for (var attempt = 1; attempt <= maxRetries; attempt++) {
            if (ct.IsCancellationRequested) return false;
            try {
                await RefreshDevices(ct);
                if (PhysicalUsb().Any(d => d.State.Equals("offline", StringComparison.OrdinalIgnoreCase))) {
                    try { await HostCommand(ct, "reconnect", "offline"); }
                    catch (Exception ex) when (ex is IOException or TimeoutException) { }
                }
                var usb = PhysicalUsb().ToArray();
                AdbDevice? candidate = null;
                if (!string.IsNullOrWhiteSpace(Serial)) {
                    candidate = usb.FirstOrDefault(d => d.Serial.Equals(Serial, StringComparison.OrdinalIgnoreCase));
                }
                if (candidate == null && !string.IsNullOrWhiteSpace(settings.DeviceSerial)) {
                    candidate = usb.FirstOrDefault(d => d.Serial.Equals(settings.DeviceSerial, StringComparison.OrdinalIgnoreCase));
                }
                if (candidate == null) {
                    var authorized = usb.Where(d => d.Authorized).ToArray();
                    if (authorized.Length == 1) candidate = authorized[0];
                }
                if (candidate != null && candidate.Authorized && candidate.State.Equals("device", StringComparison.OrdinalIgnoreCase)) {
                    Select(candidate, "usb");
                    var state = await Command(ct, "get-state");
                    if (state.Equals("device", StringComparison.OrdinalIgnoreCase)) {
                        log(L.Format("s_87ab106c548f", candidate.Display, attempt));
                        return true;
                    }
                }
            } catch (Exception) when (!ct.IsCancellationRequested) {
                // Device may still be initializing
            }
            await Task.Delay(delayMs, ct);
        }
        log(L.Get("s_43d737264fe6"));
        return false;
    }

    public async Task Connect(CancellationToken ct) {
        await RefreshDevices(ct);
        if (PhysicalUsb().Any(d => d.State.Equals("offline", StringComparison.OrdinalIgnoreCase))) {
            log(L.Get("s_74045875dd62"));
            try { await HostCommand(ct, "reconnect", "offline"); }
            catch (Exception ex) when (ex is IOException or TimeoutException) { log("ADB reconnect: " + ex.Message); }
            for (var attempt = 0; attempt < 4; attempt++) {
                await Task.Delay(500, ct);
                await RefreshDevices(ct);
                if (!PhysicalUsb().Any(d => d.State.Equals("offline", StringComparison.OrdinalIgnoreCase))) break;
            }
        }
        Device = null;
        Serial = "";
        EffectiveTransport = "";

        switch (settings.Transport) {
            case "usb":
                Select(PickUsb(), "usb");
                break;
            case "wifi":
                Select(await PickWifi(ct, allowLegacyUsb: true), "wifi");
                break;
            case "auto":
                await SelectAuto(ct);
                break;
            default:
                throw new ArgumentException(L.Get("s_fb1b82c91011") + settings.Transport);
        }

        var state = await Command(ct, "get-state");
        if (!state.Equals("device", StringComparison.OrdinalIgnoreCase))
            throw StateError(Device ?? new AdbDevice(Serial, state, "", "", "", "", EffectiveTransport == "wifi"));

        var package = await Command(ct, "shell", "pm", "path", PackageName);
        if (!package.Contains("package:", StringComparison.Ordinal))
            throw new InvalidOperationException(L.Get("s_b37891ec3bfe"));

        var model = await Command(ct, "shell", "getprop", "ro.product.model");
        if (!string.IsNullOrWhiteSpace(model) && Device is { } selected) {
            Device = selected with { Model = model.Trim() };
            var index = devices.FindIndex(d => d.Serial.Equals(selected.Serial, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) devices[index] = Device;
        }

        if (EffectiveTransport == "wifi") {
            if (Device != null && (!TrySplitNetworkSerial(Device.Serial, out var deviceHost, out _) ||
                !Settings.Ipv4(deviceHost))) settings.PhoneIp = await FindWifiIpv4(ct);
            ConfigurePcIp();
        }
        log($"Android connected · {Device?.Model ?? model} · {Serial} · {EffectiveTransport.ToUpperInvariant()}");
        try {
            await Command(ct, "shell", "cmd", "battery", "reset");
            await Command(ct, "shell", "dumpsys", "battery", "reset");
        } catch { }
    }

    private async Task SelectAuto(CancellationToken ct) {
        var preferred = FindConfiguredDevice();
        if (preferred is { IsNetwork: false }) {
            EnsureAuthorized(preferred);
            Select(preferred, "usb");
            return;
        }

        var authorizedUsb = PhysicalUsb().Where(d => d.Authorized).ToArray();
        if (authorizedUsb.Length == 1) {
            Select(authorizedUsb[0], "usb");
            return;
        }
        if (authorizedUsb.Length > 1) {
            if (preferred is { IsNetwork: true, Authorized: true }) {
                log(L.Get("s_c1f80186c64f"));
                Select(preferred, "wifi");
                return;
            }
            throw new InvalidOperationException(L.Get("s_6e7e5570a8a6") + DeviceList(authorizedUsb) +
                L.Get("s_ac5215b91128"));
        }

        if (preferred is { IsNetwork: true }) {
            EnsureAuthorized(preferred);
            Select(preferred, "wifi");
            return;
        }

        try {
            Select(await PickWifi(ct, allowLegacyUsb: false), "wifi");
            return;
        } catch (InvalidOperationException) when (PhysicalUsb().Any()) {
            // Preserve the more useful USB state error below when no working transport exists.
        }

        var presentUsb = PhysicalUsb().ToArray();
        if (presentUsb.Length > 0) throw StateError(presentUsb[0]);
        throw NoDeviceError();
    }

    private AdbDevice PickUsb() {
        var usb = PhysicalUsb().ToArray();
        var preferred = usb.FirstOrDefault(d =>
            d.Serial.Equals(settings.DeviceSerial, StringComparison.OrdinalIgnoreCase));
        if (preferred != null) {
            EnsureAuthorized(preferred);
            return preferred;
        }

        var authorized = usb.Where(d => d.Authorized).ToArray();
        if (authorized.Length == 1) return authorized[0];
        if (authorized.Length > 1)
            throw new InvalidOperationException(L.Get("s_6e7e5570a8a6") + DeviceList(authorized) +
                L.Get("s_0a207558c32e"));
        if (usb.Length > 0) throw StateError(usb[0]);

        var network = devices.Where(d => d.IsNetwork && d.Authorized).ToArray();
        if (network.Length > 0)
            throw new InvalidOperationException(L.Get("s_7ca1b140082d") + DeviceList(network) +
                L.Get("s_5684ad338370"));
        throw NoDeviceError();
    }

    private async Task<AdbDevice> PickWifi(CancellationToken ct, bool allowLegacyUsb) {
        var preferred = FindConfiguredDevice();
        if (preferred is { IsNetwork: true }) {
            EnsureAuthorized(preferred);
            UpdatePhoneEndpoint(preferred.Serial);
            return preferred;
        }

        string connectProblem = "";
        var endpoint = Settings.PhoneEndpoint(settings.PhoneIp) ? Settings.EndpointSerial(settings.PhoneIp) : "";
        if (endpoint.Length > 0) {
            var existing = devices.FirstOrDefault(d =>
                d.Serial.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                if (existing.Authorized) return existing;
                connectProblem = StateError(existing).Message;
            }

            try {
                var result = await HostCommand(ct, "connect", endpoint);
                if (!ConnectSucceeded(result)) connectProblem = result.Trim();
                await RefreshDevices(ct);
                var connected = devices.FirstOrDefault(d =>
                    d.Serial.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
                if (connected?.Authorized == true) return connected;
                if (connected != null) connectProblem = StateError(connected).Message;
            } catch (Exception ex) when (ex is IOException or TimeoutException) {
                connectProblem = ex.Message;
            }
        }

        var authorizedNetwork = devices.Where(d => d.IsNetwork && d.Authorized).ToArray();
        if (authorizedNetwork.Length == 1) {
            UpdatePhoneEndpoint(authorizedNetwork[0].Serial);
            return authorizedNetwork[0];
        }
        if (authorizedNetwork.Length > 1)
            throw new InvalidOperationException(L.Get("s_734199c9c8b9") +
                DeviceList(authorizedNetwork) + L.Get("s_0a207558c32e"));

        if (allowLegacyUsb) {
            var usb = TryPickUsbForWifi();
            if (usb != null) return await EnableLegacyWifi(usb, ct);
        }

        var blockedNetwork = devices.FirstOrDefault(d => d.IsNetwork && !d.Authorized);
        if (blockedNetwork != null) throw StateError(blockedNetwork);
        if (connectProblem.Length > 0)
            throw new InvalidOperationException(L.Get("s_c4967d0c5c24") + connectProblem);
        throw new InvalidOperationException(L.Get("s_182079663758"));
    }

    private AdbDevice? TryPickUsbForWifi() {
        var usb = PhysicalUsb().ToArray();
        var preferred = usb.FirstOrDefault(d =>
            d.Serial.Equals(settings.DeviceSerial, StringComparison.OrdinalIgnoreCase));
        if (preferred != null) {
            EnsureAuthorized(preferred);
            return preferred;
        }
        var authorized = usb.Where(d => d.Authorized).ToArray();
        if (authorized.Length == 1) return authorized[0];
        if (authorized.Length > 1)
            throw new InvalidOperationException(L.Get("s_8da98eeacf66") +
                DeviceList(authorized) + L.Get("s_0a207558c32e"));
        if (usb.Length > 0) throw StateError(usb[0]);
        return null;
    }

    private async Task<AdbDevice> EnableLegacyWifi(AdbDevice usb, CancellationToken ct) {
        Select(usb, "usb");
        var ip = await FindWifiIpv4(ct);
        log(L.Format("s_2bb018edfbf4", ip, usb.Serial));
        await Command(ct, "tcpip", "5555");

        var endpoint = ip + ":5555";
        string last = "";
        for (var attempt = 0; attempt < 12; attempt++) {
            await Task.Delay(500, ct);
            try {
                last = await HostCommand(ct, "connect", endpoint);
                if (!ConnectSucceeded(last)) continue;
                await RefreshDevices(ct);
                var connected = devices.FirstOrDefault(d =>
                    d.Serial.Equals(endpoint, StringComparison.OrdinalIgnoreCase));
                if (connected?.Authorized == true) {
                    settings.PhoneIp = ip;
                    log("Wi-Fi ADB connected · " + endpoint);
                    return connected;
                }
            } catch (Exception ex) when (ex is IOException or TimeoutException) {
                last = ex.Message;
            }
        }
        throw new IOException(L.Get("s_2e5a36cccd95") + endpoint +
            L.Get("s_912b806f9421") + last);
    }

    private async Task<string> FindWifiIpv4(CancellationToken ct) {
        foreach (var args in new[] {
            new[] { "shell", "ip", "-o", "-4", "addr", "show", "up", "scope", "global" },
            new[] { "shell", "ip", "-f", "inet", "addr", "show" }
        }) {
            try {
                var addresses = ParseInterfaceIpv4(await Command(ct, args));
                var wifi = addresses.FirstOrDefault(a => IsWifiInterface(a.Interface) && UsableAddress(a.Address));
                if (wifi != null) return wifi.Address;
                var privateAddress = addresses.FirstOrDefault(a =>
                    UsableAddress(a.Address) && IsPrivateIpv4(a.Address) && !IsNonWifiInterface(a.Interface));
                if (privateAddress != null) return privateAddress.Address;
            } catch (IOException) {
                // Older vendor builds expose only one of the two `ip` command forms.
            }
        }
        throw new InvalidOperationException(L.Get("s_c91215e38daf"));
    }

    public static string FindLocalIpv4() {
        try {
            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            route.Connect("8.8.8.8", 65530);
            var local = ((IPEndPoint)route.LocalEndPoint!).Address;
            if (!local.Equals(IPAddress.Any) && !IPAddress.IsLoopback(local))
                return local.ToString();
        } catch { }
        return "127.0.0.1";
    }

    private void ConfigurePcIp() {
        if (!settings.AutoPcIp && Settings.Ipv4(settings.PcIp)) return;
        var host = Settings.EndpointHost(settings.PhoneIp);
        if (!Settings.Ipv4(host))
            throw new InvalidOperationException(L.Get("s_895876b5a1cb"));
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(host, settings.RtpPort);
        var local = ((IPEndPoint)route.LocalEndPoint!).Address;
        if (local.Equals(IPAddress.Any) || IPAddress.IsLoopback(local))
            throw new InvalidOperationException(L.Get("s_7770790f8748") + host);
        settings.PcIp = local.ToString();
        log(L.Get("s_3cca31805023") + settings.PcIp);
    }

    private void Select(AdbDevice device, string transport) {
        EnsureAuthorized(device);
        Device = device;
        Serial = device.Serial;
        EffectiveTransport = transport;
        if (transport == "wifi") UpdatePhoneEndpoint(device.Serial);
    }

    private void UpdatePhoneEndpoint(string serial) {
        if (!TrySplitNetworkSerial(serial, out var host, out var port) || !Settings.Ipv4(host)) return;
        settings.PhoneIp = port == 5555 ? host : host + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private AdbDevice? FindConfiguredDevice() => string.IsNullOrWhiteSpace(settings.DeviceSerial) ? null :
        devices.FirstOrDefault(d => d.Serial.Equals(settings.DeviceSerial, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<AdbDevice> PhysicalUsb() => devices.Where(d =>
        !d.IsNetwork && !d.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase));

    public async Task<PhoneCapabilities> GetCapabilities(CancellationToken ct) {
        if (Device == null) throw new InvalidOperationException(L.Get("s_abc00512d845"));
        var result = await Command(ct, "shell", "content", "call", "--uri", CapabilitiesUri,
            "--method", "capabilities");
        var error = ParseBundleValue(result, "error");
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidDataException(L.Get("s_a688c90dd313") + error);
        var encoded = ParseBundleValue(result, "data");
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidDataException(L.Get("s_11a9a09a36ed"));
        try {
            var capabilities = PhoneCapabilities.ParseBase64(encoded);
            log(L.Format("s_fa738b76973c", capabilities.Cameras.Count, capabilities.Manufacturer, capabilities.Model));
            return capabilities;
        } catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) {
            throw new InvalidDataException(L.Get("s_2ac90a6852c2"), ex);
        }
    }

    public async Task Reverse(CancellationToken ct) {
        if (EffectiveTransport != "usb")
            throw new InvalidOperationException(L.Get("s_d68be15abd68"));
        var remote = $"tcp:{settings.UsbPort}";
        var local = $"tcp:{settings.UsbPort}";
        var mappings = ParseReverseMappings(await Command(ct, "reverse", "--list"));
        var existing = mappings.FirstOrDefault(m =>
            m.Remote.Equals(remote, StringComparison.OrdinalIgnoreCase) &&
            (m.Serial.Length == 0 || m.Serial.Equals(Serial, StringComparison.OrdinalIgnoreCase)));
        if (existing != null) {
            if (!existing.Local.Equals(local, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.Format("s_85e7601efa0c", remote, existing.Local));
            log(L.Get("s_cfe45c4ff17f") + remote);
            return;
        }
        await Command(ct, "reverse", remote, local);
        ownReverse = true;
        reverseSerial = Serial;
        log(L.Format("s_e8428438880b", remote, local));
    }

    public async Task StartStream(CancellationToken ct) {
        if (Device == null || EffectiveTransport is not ("usb" or "wifi"))
            throw new InvalidOperationException(L.Get("s_abc00512d845"));

        SessionId = Guid.NewGuid().ToString("N");
        await Command(ct, "shell", "input", "keyevent", "224");
        var args = new List<string> {
            "shell", "am", "start", "-W", "-n", ControlActivity,
            "-a", PackageName + ".START",
            "--es", "transport", EffectiveTransport,
            "--es", "ip", EffectiveTransport == "usb" ? "127.0.0.1" : settings.PcIp,
            "--ei", "port", (EffectiveTransport == "usb" ? settings.UsbPort : settings.RtpPort).ToString(CultureInfo.InvariantCulture),
            "--ei", "wifi_port", settings.RtpPort.ToString(CultureInfo.InvariantCulture),
            "--ei", "usb_port", settings.UsbPort.ToString(CultureInfo.InvariantCulture),
            "--ei", "fps", settings.Fps.ToString(CultureInfo.InvariantCulture),
            "--ei", "bitrate", (settings.EffectiveBitrateMbps(EffectiveTransport) * 1_000_000).ToString(CultureInfo.InvariantCulture),
            "--es", "focus", settings.Focus,
            "--ef", "focus_distance", settings.FocusDistance.ToString(CultureInfo.InvariantCulture),
            "--ei", "exposure", settings.Exposure.ToString(CultureInfo.InvariantCulture),
            "--es", "wb", settings.Wb,
            "--es", "power_mode", settings.PowerMode,
            "--es", "camera_key", settings.CameraKey,
            "--ei", "width", settings.Width.ToString(CultureInfo.InvariantCulture),
            "--ei", "height", settings.Height.ToString(CultureInfo.InvariantCulture),
            "--es", "session_id", SessionId,
            "--ez", "torch", settings.Torch ? "true" : "false",
            "--ef", "zoom", settings.Zoom.ToString(CultureInfo.InvariantCulture),
            "--ez", "lock_ae_awb", settings.LockAeAwb ? "true" : "false",
            "--ez", "face_tracking", settings.FaceTracking ? "true" : "false",
            "--ez", "auto_framing", settings.AutoFraming ? "true" : "false",
            "--ef", "auto_framing_zoom", settings.AutoFramingZoom.ToString(CultureInfo.InvariantCulture),
            "--ef", "auto_framing_speed", settings.AutoFramingSpeed.ToString(CultureInfo.InvariantCulture),
            "--ef", "auto_framing_deadzone", settings.AutoFramingDeadzone.ToString(CultureInfo.InvariantCulture),
            "--ez", "force_legacy", settings.ForceSamsungLegacy ? "true" : "false",
            "--es", "codec", settings.Codec,
            "--ei", "manual_iso", settings.ManualIso.ToString(CultureInfo.InvariantCulture),
            "--el", "shutter_speed_ns", settings.ShutterSpeedNs.ToString(CultureInfo.InvariantCulture),
            "--ei", "manual_wb_kelvin", settings.ManualWbKelvin.ToString(CultureInfo.InvariantCulture),
            "--ez", "stabilization", settings.Stabilization ? "true" : "false",
            "--es", "stabilization_mode", settings.StabilizationMode
        };
        var result = await Command(ct, args.ToArray());
        if (result.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            throw new IOException(result);

        await ConfirmStreamService(ct);
        log(L.Get("s_b389649d3de8") + SessionId[..8]);
    }

    public async Task UpdateControls(Settings nextSettings, CancellationToken ct) {
        if (Device == null || EffectiveTransport is not ("usb" or "wifi")) return;
        var args = new List<string> {
            "shell", "am", "start-foreground-service", "-n", StreamService,
            "-a", PackageName + ".UPDATE_CONTROLS",
            "--es", "transport", EffectiveTransport,
            "--es", "ip", EffectiveTransport == "usb" ? "127.0.0.1" : nextSettings.PcIp,
            "--ei", "port", (EffectiveTransport == "usb" ? nextSettings.UsbPort : nextSettings.RtpPort).ToString(CultureInfo.InvariantCulture),
            "--ei", "wifi_port", nextSettings.RtpPort.ToString(CultureInfo.InvariantCulture),
            "--ei", "usb_port", nextSettings.UsbPort.ToString(CultureInfo.InvariantCulture),
            "--ei", "fps", nextSettings.Fps.ToString(CultureInfo.InvariantCulture),
            "--ei", "bitrate", (nextSettings.EffectiveBitrateMbps(EffectiveTransport) * 1_000_000).ToString(CultureInfo.InvariantCulture),
            "--es", "focus", nextSettings.Focus,
            "--ef", "focus_distance", nextSettings.FocusDistance.ToString(CultureInfo.InvariantCulture),
            "--ei", "exposure", nextSettings.Exposure.ToString(CultureInfo.InvariantCulture),
            "--es", "wb", nextSettings.Wb,
            "--es", "camera_key", nextSettings.CameraKey,
            "--ei", "width", nextSettings.Width.ToString(CultureInfo.InvariantCulture),
            "--ei", "height", nextSettings.Height.ToString(CultureInfo.InvariantCulture),
            "--es", "session_id", SessionId,
            "--es", "power_mode", nextSettings.PowerMode,
            "--ef", "zoom", nextSettings.Zoom.ToString(CultureInfo.InvariantCulture),
            "--ez", "torch", nextSettings.Torch ? "true" : "false",
            "--ez", "lock_ae_awb", nextSettings.LockAeAwb ? "true" : "false",
            "--ez", "face_tracking", nextSettings.FaceTracking ? "true" : "false",
            "--ez", "auto_framing", nextSettings.AutoFraming ? "true" : "false",
            "--ef", "auto_framing_zoom", nextSettings.AutoFramingZoom.ToString(CultureInfo.InvariantCulture),
            "--ef", "auto_framing_speed", nextSettings.AutoFramingSpeed.ToString(CultureInfo.InvariantCulture),
            "--ef", "auto_framing_deadzone", nextSettings.AutoFramingDeadzone.ToString(CultureInfo.InvariantCulture),
            "--ez", "force_legacy", nextSettings.ForceSamsungLegacy ? "true" : "false",
            "--es", "codec", nextSettings.Codec,
            "--ei", "manual_iso", nextSettings.ManualIso.ToString(CultureInfo.InvariantCulture),
            "--el", "shutter_speed_ns", nextSettings.ShutterSpeedNs.ToString(CultureInfo.InvariantCulture),
            "--ei", "manual_wb_kelvin", nextSettings.ManualWbKelvin.ToString(CultureInfo.InvariantCulture),
            "--ez", "stabilization", nextSettings.Stabilization ? "true" : "false",
            "--es", "stabilization_mode", nextSettings.StabilizationMode
        };
        await Command(ct, args.ToArray());
    }

    public async Task RequestIdr(CancellationToken ct) {
        if (Device == null || EffectiveTransport != "usb") return;
        try {
            await Command(ct, "shell", "am", "start-foreground-service", "-n", StreamService,
                "-a", PackageName + ".REQUEST_IDR");
        } catch { }
    }

    public async Task SetBatteryCharging(bool enable, CancellationToken ct) {
        if (Device == null || string.IsNullOrWhiteSpace(Serial)) return;
        try {
            bool protect = !enable;
            if (lastBatteryProtectState == protect) return;

            // Reset any previous mock unplug states so telemetry never freezes
            await Command(ct, "shell", "cmd", "battery", "reset");
            await Command(ct, "shell", "dumpsys", "battery", "reset");

            var val = protect ? "1" : "0";
            await Command(ct, "shell", "settings", "put", "global", "protect_battery", val);
            await Command(ct, "shell", "settings", "put", "system", "protect_battery", val);
            lastBatteryProtectState = protect;

            log(protect
                ? L.Get("s_a04afb5f2922")
                : L.Get("s_117b8d05aa21"));
        } catch (Exception ex) {
            log(L.Get("s_d45c9f47cd55") + ex.Message);
        }
    }

    public async Task TapFocus(float x, float y, CancellationToken ct) {
        if (Device == null || EffectiveTransport is not ("usb" or "wifi")) return;
        await Command(ct, "shell", "am", "start-foreground-service", "-n", StreamService,
            "-a", PackageName + ".TAP_FOCUS",
            "--ef", "x", x.ToString(CultureInfo.InvariantCulture),
            "--ef", "y", y.ToString(CultureInfo.InvariantCulture));
    }

    private async Task ConfirmStreamService(CancellationToken ct) {
        string last = "";
        for (var attempt = 0; attempt < 24; attempt++) {
            await Task.Delay(250, ct);
            last = await Command(ct, "shell", "dumpsys", "activity", "services", PackageName);
            if (ServiceIsRunning(last)) return;
        }
        throw new InvalidOperationException(L.Get("s_e2769dfc9525") +
            L.Get("s_3daa47244d22") +
            (last.Contains("Permission", StringComparison.OrdinalIgnoreCase) ? " " + last.Trim() : ""));
    }

    public async Task Stop(CancellationToken ct) {
        if (Serial.Length == 0) return;
        try {
            await Command(ct, "shell", "cmd", "battery", "reset");
            await Command(ct, "shell", "dumpsys", "battery", "reset");
            lastBatteryProtectState = null;
        } catch (Exception ex) {
            log(L.Get("s_3551b1ca131a") + ex.Message);
        }
        if (SessionId.Length > 0) {
            try {
                await Command(ct, "shell", "am", "stopservice", "-n", StreamService);
            } catch (Exception ex) {
                log("STOP Android: " + ex.Message);
            }
        }
        if (ownReverse && reverseSerial.Length > 0) {
            try {
                await Processes.Run(Adb, ["-s", reverseSerial, "reverse", "--remove", $"tcp:{settings.UsbPort}"], ct);
            } catch (Exception ex) {
                log("ADB reverse cleanup: " + ex.Message);
            } finally {
                ownReverse = false;
                reverseSerial = "";
            }
        }
        SessionId = "";
    }

    private static void EnsureAuthorized(AdbDevice device) {
        if (!device.Authorized) throw StateError(device);
    }

    private static InvalidOperationException StateError(AdbDevice device) {
        var location = device.IsNetwork ? "Wi-Fi ADB" : "USB";
        var message = device.State.ToLowerInvariant() switch {
            "unauthorized" => L.Format("s_a943b4530752", device.Serial, location),
            "offline" => L.Format("s_dbd0fcc879f9", device.Serial, location),
            "authorizing" => L.Format("s_fe9443b913e2", device.Serial),
            "no permissions" => L.Format("s_9eb9d8a9bd67", device.Serial),
            "recovery" or "sideload" => L.Format("s_d1a162267662", device.Serial, device.State),
            _ => L.Format("s_df6b3e7002ad", device.Serial, location, device.State)
        };
        return new InvalidOperationException(message);
    }

    private InvalidOperationException NoDeviceError() {
        if (devices.Any(d => d.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)))
            return new InvalidOperationException(L.Get("s_ab6a739fa5c4"));
        return new InvalidOperationException(L.Get("s_f81d7171fa17"));
    }

    private static string DeviceList(IEnumerable<AdbDevice> source) => string.Join(", ",
        source.Select(d => string.IsNullOrWhiteSpace(d.Model) ? d.Serial : d.Model.Replace('_', ' ') + " (" + d.Serial + ")"));

    public static IReadOnlyList<AdbDevice> ParseDevices(string output) {
        var result = new List<AdbDevice>();
        foreach (var raw in output.Replace("\r", "").Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*')) continue;
            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2) continue;
            var serial = parts[0];
            var state = parts[1];
            var metadataStart = 2;
            if (state.Equals("no", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 &&
                parts[2].Equals("permissions", StringComparison.OrdinalIgnoreCase)) {
                state = "no permissions";
                metadataStart = 3;
            }
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = metadataStart; i < parts.Length; i++) {
                var separator = parts[i].IndexOf(':');
                if (separator <= 0) continue;
                metadata[parts[i][..separator]] = parts[i][(separator + 1)..];
            }
            metadata.TryGetValue("model", out var model);
            metadata.TryGetValue("product", out var product);
            metadata.TryGetValue("usb", out var usb);
            metadata.TryGetValue("transport_id", out var transportId);
            var isNetwork = string.IsNullOrWhiteSpace(usb) && IsNetworkSerial(serial);
            result.Add(new AdbDevice(serial, state, model ?? "", product ?? "", usb ?? "",
                transportId ?? "", isNetwork));
        }
        return result;
    }

    public static bool IsNetworkSerial(string serial) {
        if (serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase) ||
            serial.Contains("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)) return true;
        return TrySplitNetworkSerial(serial, out _, out _);
    }

    public static bool TrySplitNetworkSerial(string serial, out string host, out int port) {
        host = "";
        port = 0;
        if (serial.StartsWith('[')) {
            var end = serial.IndexOf(']');
            if (end < 0 || end + 2 >= serial.Length || serial[end + 1] != ':' ||
                !int.TryParse(serial[(end + 2)..], out port)) return false;
            host = serial[1..end];
            return port is > 0 and <= 65535;
        }
        var separator = serial.LastIndexOf(':');
        if (separator <= 0 || separator == serial.Length - 1 ||
            !int.TryParse(serial[(separator + 1)..], out port) || port is < 1 or > 65535) return false;
        host = serial[..separator];
        return host.Contains('.') || IPAddress.TryParse(host, out _);
    }

    public static bool ConnectSucceeded(string output) =>
        (output.Contains("connected to", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("already connected", StringComparison.OrdinalIgnoreCase)) &&
        !output.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
        !output.Contains("cannot", StringComparison.OrdinalIgnoreCase);

    public static string? ParseBundleValue(string output, string key) {
        var marker = key + "=";
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = output.IndexOfAny([',', '}', ']'], start);
        if (end < 0) end = output.Length;
        var value = output[start..end].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
            (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
        return value == "null" ? null : value;
    }

    public sealed record ReverseMapping(string Serial, string Remote, string Local);

    public static IReadOnlyList<ReverseMapping> ParseReverseMappings(string output) {
        var result = new List<ReverseMapping>();
        foreach (var raw in output.Replace("\r", "").Split('\n')) {
            var parts = Regex.Split(raw.Trim(), @"\s+");
            if (parts.Length >= 3) result.Add(new ReverseMapping(parts[0], parts[1], parts[2]));
            else if (parts.Length == 2) result.Add(new ReverseMapping("", parts[0], parts[1]));
        }
        return result;
    }

    public sealed record InterfaceAddress(string Interface, string Address);

    public static IReadOnlyList<InterfaceAddress> ParseInterfaceIpv4(string output) {
        var result = new List<InterfaceAddress>();
        string current = "";
        foreach (var raw in output.Replace("\r", "").Split('\n')) {
            var line = raw.Trim();
            var interfaceMatch = Regex.Match(line, @"^\d+:\s+([^:@\s]+)(?:@[^:\s]+)?:");
            if (interfaceMatch.Success) current = interfaceMatch.Groups[1].Value;
            var oneLine = Regex.Match(line, @"^\d+:\s+([^:@\s]+)(?:@[^\s]+)?\s+inet\s+(\d+\.\d+\.\d+\.\d+)/");
            if (oneLine.Success) {
                result.Add(new InterfaceAddress(oneLine.Groups[1].Value, oneLine.Groups[2].Value));
                continue;
            }
            var address = Regex.Match(line, @"^inet\s+(\d+\.\d+\.\d+\.\d+)/");
            if (address.Success && current.Length > 0)
                result.Add(new InterfaceAddress(current, address.Groups[1].Value));
        }
        return result;
    }

    public static bool ServiceIsRunning(string dumpsys) =>
        dumpsys.Contains(PackageName + "/.StreamService", StringComparison.OrdinalIgnoreCase) ||
        dumpsys.Contains(PackageName + ".StreamService", StringComparison.OrdinalIgnoreCase);

    private static bool IsWifiInterface(string name) =>
        name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("wifi", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonWifiInterface(string name) {
        var blocked = new[] { "lo", "rmnet", "ccmni", "pdp", "dummy", "tun", "rndis", "usb", "ap", "swlan" };
        return blocked.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsableAddress(string value) => Settings.Ipv4(value) &&
        !value.StartsWith("127.", StringComparison.Ordinal) &&
        !value.StartsWith("169.254.", StringComparison.Ordinal) && value != "0.0.0.0";

    private static bool IsPrivateIpv4(string value) {
        var bytes = IPAddress.Parse(value).GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}
