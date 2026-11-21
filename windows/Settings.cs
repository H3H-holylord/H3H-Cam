using System.IO;
using System.Net;
using System.Text.Json;

namespace S8Cam;

public sealed class Settings {
    public static int ActiveInstance { get; set; } = 1;

    public string Language { get; set; } = "auto";
    public string UiTheme { get; set; } = "cine_rig"; // "cine_rig" | "nordic_atelier"
    public bool MiniControllerMode { get; set; } = false;
    public string PrivacySlateMode { get; set; } = "studio_card"; // "studio_card" | "blurred_freeze" | "custom_image" | "black"
    public string PrivacySlateTitle { get; set; } = "СКОРО ВЕРНУСЬ";
    public string PrivacySlateImage { get; set; } = "";
    public string LutPath { get; set; } = "none"; // "none" | "builtin:kodak2383" | "builtin:arri_warm" | "builtin:nordic_clean" | file.cube
    public float LutStrength { get; set; } = 0.85f;
    public string VideoScopeMode { get; set; } = "none"; // "none" | "waveform" | "parade" | "histogram"
    public bool FalseColor { get; set; } = false;
    public float FocusSlotA { get; set; } = 0.35f;
    public float FocusSlotB { get; set; } = 2.50f;
    public float FocusPullDurationSec { get; set; } = 0.80f;
    public bool AudioEnabled { get; set; } = false;
    public int AudioPort { get; set; } = 5004;
    public string AudioSource { get; set; } = "voice_comm"; // "voice_comm" | "camcorder" | "unprocessed" | "mic"
    public bool AudioNoiseSuppression { get; set; } = true;
    public double AudioGainDb { get; set; } = 0.0;
    public bool AudioMuted { get; set; } = false;
    public bool AudioMonitorEnabled { get; set; } = true;
    public int AudioDelayMs { get; set; } = 45;
    public string AudioOutputDevice { get; set; } = "default";
    public string SpoutChannelName { get; set; } = "H3HCam";
    public bool ObsWsEnabled { get; set; } = false;
    public string ObsWsUrl { get; set; } = "ws://127.0.0.1:4455";
    public string ObsWsPassword { get; set; } = "";
    public string ObsTargetScene { get; set; } = "";
    public string ObsAudioInputName { get; set; } = "";
    public bool ObsEcoMode { get; set; } = false;
    public string Transport { get; set; } = "auto";
    [System.Text.Json.Serialization.JsonIgnore] public string WifiPairSession { get; set; } = "";
    public string PhoneIp { get; set; } = "192.168.1.100";
    public string PcIp { get; set; } = "";
    public bool AutoPcIp { get; set; } = true;
    public string DeviceSerial { get; set; } = "";
    public int RtpPort { get; set; } = 5000;
    public int UsbPort { get; set; } = 5002;
    public int ObsPort { get; set; } = 5001;
    public int PreviewPort { get; set; } = 5003;
    public int Fps { get; set; } = 30;
    public int BitrateMbps { get; set; } = 20;
    public string CameraKey { get; set; } = "auto";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool Preview { get; set; } = true;
    public bool Obs { get; set; } = true;
    public bool VirtualCamera { get; set; } = true;
    public bool LowLatency { get; set; } = true;
    public bool WifiFriendly { get; set; }
    public int WifiLimitMbps { get; set; }
    public bool AdaptiveBitrate { get; set; } = true;
    public int MinimumWifiBitrateMbps { get; set; } = 12;
    public string PowerMode { get; set; } = "balanced";
    public bool ScreenOff { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool AutoStartOnUsb { get; set; } = false;
    public bool RunOnStartup { get; set; } = false;
    public bool FlipHorizontal { get; set; } = false;
    public int Rotation { get; set; } = 0;
    public double Brightness { get; set; } = 0.0;
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public bool PrivacyMute { get; set; } = false;
    public bool ThermalGuard { get; set; } = false;
    public int ThermalThresholdC { get; set; } = 52;
    public bool SpoutOutput { get; set; } = false;
    public string Focus { get; set; } = "continuous";
    public float FocusDistance { get; set; }
    public int Exposure { get; set; }
    public string Wb { get; set; } = "auto";
    public bool Torch { get; set; } = false;
    public float Zoom { get; set; } = 1.0f;
    public bool LockAeAwb { get; set; } = false;
    public bool Stabilization { get; set; } = true;
    public string StabilizationMode { get; set; } = "strong";
    public bool FaceTracking { get; set; } = true;
    public bool AutoFraming { get; set; } = false;
    public float AutoFramingZoom { get; set; } = 1.35f;
    public float AutoFramingSpeed { get; set; } = 1.0f;
    public float AutoFramingDeadzone { get; set; } = 0.05f;
    public bool CompositionGrid { get; set; } = false;
    public bool FocusPeaking { get; set; } = false;
    public bool ZebraPattern { get; set; } = false;
    public string ColorProfile { get; set; } = "none";
    public string BackgroundEffect { get; set; } = "none";
    public int BackgroundBlurStrength { get; set; } = 15;
    public bool AiSegmentation { get; set; } = true;
    public string CustomBackgroundImage { get; set; } = "";
    public float AiEdgeFeather { get; set; } = 0.15f;
    public bool AiTransparentSpout { get; set; } = false;
    public bool SkinSmoothing { get; set; } = false;
    public bool BatteryProtect { get; set; } = true;
    public bool ForceSamsungLegacy { get; set; } = false;
    public string CameraApi { get; set; } = "auto"; // "auto", "camera2", "camera1"
    public string EffectiveCameraApi =>
        CameraApi is "camera1" or "camera2" ? CameraApi : (ForceSamsungLegacy ? "camera1" : "auto");
    public string Codec { get; set; } = "h264";
    public int ManualIso { get; set; } = 0;
    public long ShutterSpeedNs { get; set; } = 0L;
    public int ManualWbKelvin { get; set; } = 0;
    public float WbRedGain { get; set; } = 1.0f;
    public float WbBlueGain { get; set; } = 1.0f;
    public float WbGreenGain { get; set; } = 1.0f;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool ShowHud { get; set; } = false;
    public double HudLeft { get; set; } = -1;
    public double HudTop { get; set; } = -1;
    public bool ShowFaceOverlay { get; set; } = true;
    public bool SuperResolution4K { get; set; } = false;
    public float SuperResolutionSharpness { get; set; } = 0.20f;
    public string OrientationMode { get; set; } = "16:9"; // "16:9", "9:16_crop", "9:16_autoframing", "9:16_fit"
    public bool ProMode { get; set; } = false;
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 900;
    public string QualityProfile { get; set; } = "balanced"; // "balanced", "quality", "low_latency", "battery_saver", "custom"
    public string AdbPath { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfplayPath { get; set; } = "";
    public string ObsPath { get; set; } = "";
    public Dictionary<string, Settings> CustomPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public (int Width, int Height) OutputDimensions {
        get {
            var isPortrait = OrientationMode.StartsWith("9:16", StringComparison.OrdinalIgnoreCase) || Rotation is 90 or 270;
            return isPortrait ? (Height, Width) : (Width, Height);
        }
    }

    public (int Width, int Height) FinalOutputDimensions =>
        SuperResolution4K ? ((OutputDimensions.Width < OutputDimensions.Height) ? (2160, 3840) : (3840, 2160)) : OutputDimensions;

    public string BuildVideoFilter(int targetWidth, int targetHeight, bool nv12Limited601 = false) {
        if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        var filters = new List<string>();
        // Raw pipes carry no color metadata; CPU/GPU NV12 converters use limited BT.601.
        var nv12Color = nv12Limited601 ? ":out_color_matrix=bt601:out_range=tv" : "";
        if (PrivacyMute) {
            filters.Add("drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill");
        }
        if (FlipHorizontal) filters.Add("hflip");
        switch (Rotation) {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("hflip,vflip"); break;
            case 270: filters.Add("transpose=2"); break;
        }

        if (OrientationMode is "9:16_crop" or "9:16_autoframing") {
            // Fit a centered 9:16 rectangle inside either landscape or already rotated input.
            // Even dimensions are required for chroma-subsampled video.
            filters.Add("crop=w='trunc(min(iw,ih*9/16)/2)*2':h='trunc(min(ih,iw*16/9)/2)*2'");
        } else if (OrientationMode == "9:16_fit") {
            filters.Add($"scale={targetWidth}:{targetHeight}:force_original_aspect_ratio=decrease{nv12Color},pad={targetWidth}:{targetHeight}:(ow-iw)/2:(oh-ih)/2");
            return string.Join(",", filters);
        }

        filters.Add($"scale={targetWidth}:{targetHeight}:flags=fast_bilinear{nv12Color}");
        return string.Join(",", filters);
    }

    public int EffectiveBitrateMbps(string transport) {
        if (transport != "wifi") return BitrateMbps;
        var limit = WifiLimitMbps;
        return limit > 0 ? Math.Min(BitrateMbps, limit) : BitrateMbps;
    }

    // These changes require new transport bindings, SDP or decoder dimensions.
    public bool RequiresStreamRestart(Settings next) =>
        Width != next.Width || Height != next.Height || Fps != next.Fps || Codec != next.Codec ||
        CameraKey != next.CameraKey || EffectiveCameraApi != next.EffectiveCameraApi ||
        Transport != next.Transport || DeviceSerial != next.DeviceSerial || PhoneIp != next.PhoneIp ||
        PcIp != next.PcIp || AutoPcIp != next.AutoPcIp || RtpPort != next.RtpPort || UsbPort != next.UsbPort ||
        ObsPort != next.ObsPort || PreviewPort != next.PreviewPort || AudioPort != next.AudioPort || Obs != next.Obs ||
        AdbPath != next.AdbPath || FfmpegPath != next.FfmpegPath;

    public Settings Clone() {
        var clone = (Settings)MemberwiseClone();
        clone.CustomPresets = new Dictionary<string, Settings>(CustomPresets, StringComparer.OrdinalIgnoreCase);
        return clone;
    }

    public void CopyCapturePropertiesFrom(Settings source) {
        Width = source.Width;
        Height = source.Height;
        Fps = source.Fps;
        BitrateMbps = source.BitrateMbps;
        Codec = source.Codec;
        LowLatency = source.LowLatency;
        Preview = source.Preview;
        Obs = source.Obs;
        VirtualCamera = source.VirtualCamera;
        SpoutOutput = source.SpoutOutput;
        if (!string.IsNullOrWhiteSpace(source.SpoutChannelName)) SpoutChannelName = source.SpoutChannelName;
        WifiFriendly = source.WifiFriendly;
        WifiLimitMbps = source.WifiLimitMbps;
        AdaptiveBitrate = source.AdaptiveBitrate;
        MinimumWifiBitrateMbps = source.MinimumWifiBitrateMbps;
        PowerMode = source.PowerMode;
        ScreenOff = source.ScreenOff;
        BatteryProtect = source.BatteryProtect;
        if (!string.IsNullOrEmpty(source.CameraKey)) CameraKey = source.CameraKey;
        Zoom = source.Zoom;
        FlipHorizontal = source.FlipHorizontal;
        Rotation = source.Rotation;
        OrientationMode = source.OrientationMode;
        Brightness = source.Brightness;
        Contrast = source.Contrast;
        Saturation = source.Saturation;
        ColorProfile = source.ColorProfile;
        LutPath = source.LutPath;
        LutStrength = source.LutStrength;
        VideoScopeMode = source.VideoScopeMode;
        FalseColor = source.FalseColor;
        FocusSlotA = source.FocusSlotA;
        FocusSlotB = source.FocusSlotB;
        FocusPullDurationSec = source.FocusPullDurationSec;
        AudioEnabled = source.AudioEnabled;
        AudioSource = source.AudioSource;
        AudioNoiseSuppression = source.AudioNoiseSuppression;
        AudioGainDb = source.AudioGainDb;
        AudioDelayMs = source.AudioDelayMs;
        BackgroundEffect = source.BackgroundEffect;
        BackgroundBlurStrength = source.BackgroundBlurStrength;
        AiSegmentation = source.AiSegmentation;
        CustomBackgroundImage = source.CustomBackgroundImage;
        AiEdgeFeather = source.AiEdgeFeather;
        AiTransparentSpout = source.AiTransparentSpout;
        SkinSmoothing = source.SkinSmoothing;
        SuperResolution4K = source.SuperResolution4K;
        SuperResolutionSharpness = source.SuperResolutionSharpness;
        AutoFraming = source.AutoFraming;
        AutoFramingZoom = source.AutoFramingZoom;
        AutoFramingSpeed = source.AutoFramingSpeed;
        AutoFramingDeadzone = source.AutoFramingDeadzone;
        FaceTracking = source.FaceTracking;
        Stabilization = source.Stabilization;
        StabilizationMode = source.StabilizationMode;
        Focus = source.Focus;
        FocusDistance = source.FocusDistance;
        Exposure = source.Exposure;
        Wb = source.Wb;
        Torch = source.Torch;
        LockAeAwb = source.LockAeAwb;
        ManualIso = source.ManualIso;
        ShutterSpeedNs = source.ShutterSpeedNs;
        ManualWbKelvin = source.ManualWbKelvin;
        WbRedGain = source.WbRedGain;
        WbBlueGain = source.WbBlueGain;
        WbGreenGain = source.WbGreenGain;
        CompositionGrid = source.CompositionGrid;
        FocusPeaking = source.FocusPeaking;
        ZebraPattern = source.ZebraPattern;
        ThermalGuard = source.ThermalGuard;
        ThermalThresholdC = source.ThermalThresholdC;
        ForceSamsungLegacy = source.ForceSamsungLegacy;
        CameraApi = source.CameraApi;
        QualityProfile = source.QualityProfile;
    }

    private static string? DataDirOverride => Environment.GetEnvironmentVariable("H3HCAM_DATA_DIR");
    public static string DataDir {
        get {
            if (!string.IsNullOrWhiteSpace(DataDirOverride)) return Path.GetFullPath(DataDirOverride);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H3HCam");
            return ActiveInstance > 1 ? Path.Combine(root, $"instance-{ActiveInstance}") : root;
        }
    }
    public static string FilePath => Path.Combine(DataDir, "settings.json");

    public static string LegacyDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "S8Cam");
    public static string LegacyFilePath => Path.Combine(LegacyDataDir, "settings.json");

    public static Settings CreateDefaultForInstance(int instance) {
        var s = new Settings();
        if (instance > 1) {
            int offset = (instance - 1) * 10;
            s.RtpPort = 5000 + offset;
            s.ObsPort = 5001 + offset;
            s.UsbPort = 5002 + offset;
            s.PreviewPort = 5003 + offset;
            s.AudioPort = 5004 + offset;
            s.SpoutChannelName = $"H3HCam_{instance}";
            s.SpoutOutput = true;
            s.VirtualCamera = false;
        }
        return s;
    }

    public static Settings Load(string? filePath = null) {
        var path = filePath ?? FilePath;
        if (filePath == null && ActiveInstance <= 1 && string.IsNullOrWhiteSpace(DataDirOverride) && !File.Exists(path) && File.Exists(LegacyFilePath)) {
            try {
                Directory.CreateDirectory(DataDir);
                File.Copy(LegacyFilePath, FilePath, true);
            } catch { }
        }
        if (!File.Exists(path)) return CreateDefaultForInstance(ActiveInstance);
        try {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? CreateDefaultForInstance(ActiveInstance);
            s.CustomPresets ??= new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(s.SpoutChannelName)) {
                s.SpoutChannelName = ActiveInstance > 1 ? $"H3HCam_{ActiveInstance}" : "H3HCam";
            }
            if (s.CameraApi is not ("auto" or "camera2" or "camera1")) {
                s.CameraApi = s.ForceSamsungLegacy ? "camera1" : "auto";
            } else if (s.CameraApi == "auto" && s.ForceSamsungLegacy) {
                s.CameraApi = "camera1";
            }
            // Preserve explicit user choices. Device mode compatibility is checked after discovery.
            return s;
        } catch (JsonException) {
            File.Copy(path, path + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            return CreateDefaultForInstance(ActiveInstance);
        }
    }

    public void Save() {
        Directory.CreateDirectory(DataDir);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }

    public void Validate(string? effectiveTransport = null) {
        var transport = effectiveTransport ?? Transport;
        if (Transport is not ("auto" or "wifi" or "usb" or "direct") || transport is not ("auto" or "wifi" or "usb" or "direct"))
            throw new ArgumentException(L.Get("s_791ed1bb43ac"));
        if (Fps is < 10 or > 60 || BitrateMbps is < 1 or > 80)
            throw new ArgumentException(L.Get("s_5d3dc1bc6b81"));
        if (WifiLimitMbps is not (0 or 12 or 20 or 32))
            throw new ArgumentException(L.Get("s_c908b4d95c88"));
        if (MinimumWifiBitrateMbps is < 4 or > 32)
            throw new ArgumentException(L.Get("s_90d9aad47dc6"));
        if (Width is < 320 or > 4096 || Height is < 240 or > 2160)
            throw new ArgumentException(L.Get("s_1d671cfd8ae9"));
        if (new[] { RtpPort, UsbPort, ObsPort, PreviewPort, AudioPort }.Any(p => p is < 1024 or > 65534))
            throw new ArgumentException(L.Get("s_1bfbfdf0fbf0"));
        var udp = new[] { RtpPort, ObsPort, PreviewPort, AudioPort };
        if (udp.Distinct().Count() != udp.Length)
            throw new ArgumentException(L.Get("s_b5ed8f998589"));
        if (transport == "wifi" && (!PhoneEndpoint(PhoneIp) || !Ipv4(PcIp)))
            throw new ArgumentException(L.Get("s_cb3d66f63eaf"));
        if (Focus is not ("continuous" or "auto" or "infinity" or "manual") ||
            !float.IsFinite(FocusDistance) || FocusDistance < 0)
            throw new ArgumentException(L.Get("s_3917c61ca304"));
        if (Wb is not ("auto" or "daylight" or "cloudy" or "incandescent" or "fluorescent"))
            throw new ArgumentException(L.Get("s_538158b4f73e"));
        if (PowerMode is not ("maximum" or "balanced" or "saving")) throw new ArgumentException(L.Get("s_ac1a35870437"));
        if (!float.IsFinite(Zoom) || Zoom is < 1.0f or > 10.0f) throw new ArgumentException(L.Get("s_05ff313dc5f9"));
        if (!float.IsFinite(AutoFramingZoom) || AutoFramingZoom is < 1.0f or > 3.0f) throw new ArgumentException(L.Get("s_dd8f2240d2a7"));
        if (!float.IsFinite(AutoFramingSpeed) || AutoFramingSpeed is < 0.1f or > 5.0f) throw new ArgumentException(L.Get("s_b9791ed73d36"));
        if (!float.IsFinite(AutoFramingDeadzone) || AutoFramingDeadzone is < 0.005f or > 0.5f) throw new ArgumentException(L.Get("s_4b57a44e40ae"));
        if (Codec is not ("h264" or "hevc")) throw new ArgumentException(L.Get("s_62acd68dd765"));
        if (OrientationMode is not ("16:9" or "9:16_crop" or "9:16_fit" or "9:16_autoframing"))
            throw new ArgumentException(L.Get("s_25e7dd1154ff"));
        if (Rotation is not (0 or 90 or 180 or 270)) throw new ArgumentException(L.Get("s_32c415ce3e2b"));
        if (ManualIso is < 0 or > 25600) throw new ArgumentException("ISO: 0–25600");
        if (ShutterSpeedNs is < 0 or > 1_000_000_000L) throw new ArgumentException("Shutter: 0–1s");
        if (ManualWbKelvin < 0 || (ManualWbKelvin > 0 && (ManualWbKelvin < 2000 || ManualWbKelvin > 10000))) throw new ArgumentException("WB Kelvin: 2000–10000");
        if (!float.IsFinite(LutStrength) || LutStrength is < 0f or > 1f) throw new ArgumentException("LUT Strength: 0.0–1.0");
        if (!float.IsFinite(FocusPullDurationSec) || FocusPullDurationSec is < 0.05f or > 10.0f) throw new ArgumentException("Focus Pull Duration: 0.05–10s");
        if (AudioSource is not ("voice_comm" or "camcorder" or "unprocessed")) AudioSource = "voice_comm";
        if (string.IsNullOrWhiteSpace(SpoutChannelName)) SpoutChannelName = "H3HCam";
        if (!Preview && !Obs && !VirtualCamera && !SpoutOutput) throw new ArgumentException(L.Get("s_ac16ac51f54c"));
    }

    public static bool Ipv4(string value) =>
        IPAddress.TryParse(value, out var ip) &&
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        value.Count(c => c == '.') == 3;

    public static bool PhoneEndpoint(string value) {
        if (Ipv4(value)) return true;
        var split = value.Split(':');
        return split.Length == 2 && Ipv4(split[0]) && int.TryParse(split[1], out var port) &&
            port is > 0 and <= 65535;
    }

    public static string EndpointHost(string value) => value.Split(':')[0];
    public static string EndpointSerial(string value) => value.Contains(':') ? value : value + ":5555";
}
