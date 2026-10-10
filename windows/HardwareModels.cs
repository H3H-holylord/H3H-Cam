using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S8Cam;

public sealed record AdbDevice(
    string Serial,
    string State,
    string Model,
    string Product,
    string Usb,
    string TransportId,
    bool IsNetwork)
{
    public bool Authorized => State == "device";
    public string Display => $"{(string.IsNullOrWhiteSpace(Model) ? Serial : Model.Replace('_', ' '))} · " +
        $"{(IsNetwork ? "Wi-Fi ADB" : "USB")} · {State} · {Serial}";
}

public sealed class PhoneCapabilities {
    public int Version { get; set; }
    public string Model { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public int Sdk { get; set; }
    public List<CameraCapability> Cameras { get; set; } = [];

    public static PhoneCapabilities ParseBase64(string encoded) {
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return ParseJson(json);
    }

    public static PhoneCapabilities ParseJson(string json) {
        return JsonSerializer.Deserialize<PhoneCapabilities>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException(L.Get("s_c6f27c85bfa5"));
    }
}

public sealed class CameraCapability {
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Facing { get; set; } = "";
    public float MinimumFocusDistance { get; set; }
    public bool Flash { get; set; }
    public List<VideoCapability> Modes { get; set; } = [];
    [JsonIgnore] public string DisplayLabel => L.PhoneText(L.Relocalize(Label));
    public override string ToString() => DisplayLabel;
}

public sealed class VideoCapability {
    public int Width { get; set; }
    public int Height { get; set; }
    public List<int> Fps { get; set; } = [];
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    [JsonIgnore] public bool Scaled => CaptureWidth > 0 && CaptureHeight > 0 && (CaptureWidth != Width || CaptureHeight != Height);
    [JsonIgnore] public string Key => $"{Width}x{Height}";
    [JsonIgnore] public string Label => $"{Width} × {Height}" +
        (Scaled ? L.Format("s_a8dd8f824e26", CaptureWidth, CaptureHeight) : "");
    public override string ToString() => Label;
}
