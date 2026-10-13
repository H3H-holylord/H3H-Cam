using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace S8Cam;

/// <summary>A short-lived, user-opened QR invitation. Never saved to disk.</summary>
public sealed class WifiPairingTicket {
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public string? PhoneIp { get; private set; }
    public Settings? ReadySettings { get; set; }
    public volatile bool StopRequested;
    public TaskCompletionSource StopDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Claim(string token, IPAddress address) {
        if (DateTimeOffset.UtcNow >= Expires || token != Token ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        if (PhoneIp != null && PhoneIp != address.ToString()) return false;
        PhoneIp = address.ToString();
        return true;
    }
}

public sealed record WifiPairRequest(string Ip, string Token, PhoneCapabilities Capabilities);

public static class WifiPairing {
    public static object StreamCommand(Settings s, string command = "START_STREAM") => new {
        command, cameraKey = s.CameraKey, width = s.Width, height = s.Height, fps = s.Fps,
        bitrate = s.EffectiveBitrateMbps("wifi") * 1_000_000, codec = s.Codec,
        focus = s.Focus, focusDistance = s.FocusDistance, exposure = s.Exposure, wb = s.Wb,
        powerMode = s.PowerMode, torch = s.Torch, zoom = s.Zoom, lockAeAwb = s.LockAeAwb,
        faceTracking = s.FaceTracking, autoFraming = s.AutoFraming, autoFramingZoom = s.AutoFramingZoom,
        autoFramingSpeed = s.AutoFramingSpeed, autoFramingDeadzone = s.AutoFramingDeadzone,
        manualIso = s.ManualIso, shutterSpeedNs = s.ShutterSpeedNs, manualWbKelvin = s.ManualWbKelvin,
        stabilization = s.Stabilization, stabilizationMode = s.StabilizationMode
    };
    public static byte[] Control(object command, string session) {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(command))!;
        if (session.Length > 0) node["session"] = session;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }
}
