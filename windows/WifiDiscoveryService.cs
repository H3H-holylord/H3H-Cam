using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace S8Cam;

public sealed record DiscoveredPhone(string Ip, string Model, DateTime Time);

/// <summary>
/// Zero-Config UDP discovery service for wireless pairing between PC and phone.
/// Broadcasts presence beacon on port 5005 and auto-responds to phone discovery requests.
/// </summary>
public sealed class WifiDiscoveryService : IDisposable {
    public const int DiscoveryPort = 5005;
    private readonly Func<Settings> getSettings;
    private readonly Action<string> log;
    private readonly CancellationTokenSource cts = new();
    private UdpClient? udp;
    private Task? listenTask;
    private Task? broadcastTask;
    private bool disposed;

    public event Action<DiscoveredPhone>? PhoneDiscovered;

    public WifiDiscoveryService(Func<Settings> getSettings, Action<string> log) {
        this.getSettings = getSettings;
        this.log = log;
    }

    public void Start() {
        if (disposed || listenTask != null) return;
        try {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            udp.EnableBroadcast = true;
        } catch (Exception ex) {
            log("Wi-Fi Discovery bind: " + ex.Message);
            return;
        }

        listenTask = ListenLoop(cts.Token);
        broadcastTask = BroadcastLoop(cts.Token);
    }

    public static string ResolveBestLocalIpv4(IPAddress? remote = null) {
        if (remote != null && !IPAddress.IsLoopback(remote) && !remote.Equals(IPAddress.Any) && !remote.Equals(IPAddress.Broadcast)) {
            try {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(remote, 65530);
                if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address) && !ep.Address.Equals(IPAddress.Any)) {
                    return ep.Address.ToString();
                }
            } catch { }
        }
        try {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var ip in props.UnicastAddresses) {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address)) {
                        var str = ip.Address.ToString();
                        if (str.StartsWith("192.168.") || str.StartsWith("10.") || str.StartsWith("172.")) {
                            return str;
                        }
                    }
                }
            }
        } catch { }
        return AdbController.FindLocalIpv4();
    }

    private async Task ListenLoop(CancellationToken ct) {
        var buf = new byte[2048];
        while (!ct.IsCancellationRequested && udp != null) {
            try {
                var result = await udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.Contains("H3HCAM_DISCOVERY_PING") || text.Contains("\"action\":\"ping\"")) {
                    var s = getSettings();
                    var pcIp = !string.IsNullOrWhiteSpace(s.PcIp) && s.PcIp != "127.0.0.1" && s.PcIp != "0.0.0.0"
                        ? s.PcIp
                        : ResolveBestLocalIpv4(result.RemoteEndPoint.Address);
                    var replyJson = JsonSerializer.Serialize(new {
                        service = "h3hcam",
                        pc_name = Environment.MachineName,
                        pc_ip = pcIp,
                        port = s.RtpPort,
                        codec = s.Codec,
                        fps = s.Fps
                    });
                    var replyBytes = Encoding.UTF8.GetBytes("H3HCAM_PONG " + replyJson);
                    await udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);

                    // Extract phone name if provided
                    var phoneModel = "Android Phone";
                    try {
                        using var doc = JsonDocument.Parse(text.StartsWith("H3HCAM_DISCOVERY_PING ") ? text[22..] : text);
                        if (doc.RootElement.TryGetProperty("model", out var m)) phoneModel = m.GetString() ?? phoneModel;
                    } catch { }

                    var phone = new DiscoveredPhone(result.RemoteEndPoint.Address.ToString(), phoneModel, DateTime.Now);
                    PhoneDiscovered?.Invoke(phone);
                }
            } catch (OperationCanceledException) { break; }
            catch (Exception) { /* ignore packet noise */ }
        }
    }

    private async Task BroadcastLoop(CancellationToken ct) {
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        while (!ct.IsCancellationRequested && udp != null) {
            try {
                var s = getSettings();
                var pcIp = !string.IsNullOrWhiteSpace(s.PcIp) && s.PcIp != "127.0.0.1" && s.PcIp != "0.0.0.0"
                    ? s.PcIp
                    : ResolveBestLocalIpv4();
                if (!string.IsNullOrWhiteSpace(pcIp) && pcIp != "127.0.0.1") {
                    var beacon = JsonSerializer.Serialize(new {
                        service = "h3hcam",
                        pc_name = Environment.MachineName,
                        pc_ip = pcIp,
                        port = s.RtpPort,
                        codec = s.Codec,
                        fps = s.Fps
                    });
                    var bytes = Encoding.UTF8.GetBytes("H3HCAM_BEACON " + beacon);
                    await udp.SendAsync(bytes, bytes.Length, broadcastEndpoint);
                }
            } catch { }
            await Task.Delay(2000, ct);
        }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        udp?.Dispose();
        udp = null;
        cts.Dispose();
    }
}
