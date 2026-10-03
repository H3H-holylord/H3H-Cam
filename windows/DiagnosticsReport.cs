using System.Reflection;
using System.Text;

namespace S8Cam;

public static class DiagnosticsReport {
    public static string AppVersion {
        get {
            try {
                var asm = typeof(DiagnosticsReport).Assembly;
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
                var ver = asm.GetName().Version;
                if (ver != null) return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                return "4.0.0";
            } catch {
                return "4.0.0";
            }
        }
    }

    public static string Generate(
        Settings settings,
        LiveStatus? status,
        AdbDevice? adbDevice,
        ConnectionManager? connectionManager) {

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                      H3H CAM 4.0 DIAGNOSTICS REPORT");
        sb.AppendLine($" Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | App Version: {AppVersion} | OS: {Environment.OSVersion}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // 1. Android Device
        sb.AppendLine("[1. ANDROID DEVICE & HARDWARE]");
        sb.AppendLine($"  Device Model:         {adbDevice?.Model ?? "unavailable"}");
        sb.AppendLine($"  Serial Number:        {status?.Serial ?? settings.DeviceSerial ?? "—"}");
        sb.AppendLine($"  Selected Camera:      {settings.CameraKey}");
        sb.AppendLine($"  Selected Resolution:  {settings.Width}x{settings.Height} (Output: {settings.OutputDimensions.Width}x{settings.OutputDimensions.Height})");
        sb.AppendLine($"  Selected Codec:       {settings.Codec.ToUpperInvariant()} (Hardware MediaCodec)");
        sb.AppendLine($"  Requested FPS:        {settings.Fps} FPS");
        sb.AppendLine($"  Actual Encoded FPS:   {(status != null ? status.DetectedFps.ToString("F1") : "—")} FPS");
        sb.AppendLine($"  Requested Bitrate:    {settings.BitrateMbps} Mbps");
        sb.AppendLine($"  Actual Bitrate:       {(status != null ? status.ReceivedMbps.ToString("F2") : "—")} Mbps");
        sb.AppendLine($"  Battery Level:        {(status != null && status.Power.Percent.HasValue ? status.Power.Percent.Value + "%" : "unavailable")}");
        sb.AppendLine($"  Battery Temperature:  {(status != null && status.Power.TemperatureC.HasValue ? status.Power.TemperatureC.Value.ToString("F1") + " °C" : "unavailable")}");
        sb.AppendLine($"  Battery Power:        {(status != null && status.Power.PowerW.HasValue ? status.Power.PowerW.Value.ToString("F2") + " W" : "unavailable")}");
        sb.AppendLine($"  Protect Battery:      {(settings.BatteryProtect ? "Requested (firmware support not verified)" : "Disabled")}");
        sb.AppendLine();

        // 2. Transport Layer
        sb.AppendLine("[2. UNIFIED TRANSPORT LAYER]");
        sb.AppendLine($"  Configured Transport: {settings.Transport.ToUpperInvariant()}");
        sb.AppendLine($"  Active Transport:     {status?.Transport ?? "—"}");
        sb.AppendLine($"  Transport State:      {connectionManager?.State.ToString() ?? "—"}");
        sb.AppendLine($"  Reconnect Attempts:   {connectionManager?.ReconnectAttempts ?? 0}");
        sb.AppendLine($"  Elapsed Uptime:       {(status != null ? status.Elapsed.ToString(@"hh\:mm\:ss") : "—")}");
        sb.AppendLine($"  Packets Received:     {(status != null ? status.Packets.ToString("N0") : "—")}");
        sb.AppendLine($"  Lost Packets:         {(status != null ? status.LostPackets.ToString("N0") : "0")}");
        sb.AppendLine($"  Recovered Packets:    {(status != null ? status.RecoveredPackets.ToString("N0") : "0")}");
        sb.AppendLine($"  RTP jitter (not total latency):    {(status != null ? status.LatencyMs.ToString("F1") + " ms" : "—")}");
        sb.AppendLine();

        // 3. Windows Processing & AI
        sb.AppendLine("[3. WINDOWS RECEIVER & PROCESSING]");
        sb.AppendLine($"  Application Version:  {AppVersion}");
        sb.AppendLine($"  Protocol Version:     H3H Protocol v{H3HProtocol.CurrentVersion} (negotiated)");
        sb.AppendLine($"  Super Resolution:     {(settings.SuperResolution4K ? $"4K Direct3D 11 GPU (Sharpness: {(int)(settings.SuperResolutionSharpness * 100)}%)" : "Off")}");
        sb.AppendLine($"  AI Segmentation:      {(settings.AiSegmentation ? "Enabled (DirectML ONNX)" : "Off")}");
        sb.AppendLine($"  Background Effect:    {settings.BackgroundEffect}");
        sb.AppendLine($"  Face Tracking:        {(settings.FaceTracking ? "Enabled" : "Off")}");
        sb.AppendLine($"  Auto Framing:         {(settings.AutoFraming ? $"Enabled (Zoom: {settings.AutoFramingZoom:F2}x, Speed: {settings.AutoFramingSpeed:F1}x)" : "Off")}");
        sb.AppendLine();

        // 4. Output Status
        sb.AppendLine("[4. OUTPUT SINK CHANNELS]");
        sb.AppendLine($"  Spout2 Direct (OBS):  {(settings.SpoutOutput ? $"ACTIVE (Sender: H3HCam, {settings.FinalOutputDimensions.Width}x{settings.FinalOutputDimensions.Height})" : "Disabled")}");
        sb.AppendLine($"  Virtual Camera:       {(settings.VirtualCamera ? "ACTIVE (DirectShow OBS-VirtualCam filter)" : "Disabled")}");
        sb.AppendLine($"  Local Preview:        {(settings.Preview ? "Enabled (Low-delay Direct3D/WPF)" : "Disabled")}");
        sb.AppendLine($"  MPEG-TS Relay (OBS):  {(settings.Obs ? $"Enabled (UDP port {settings.ObsPort})" : "Disabled")}");
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                     END OF DIAGNOSTICS REPORT");
        sb.AppendLine("================================================================================");

        return sb.ToString();
    }
}

