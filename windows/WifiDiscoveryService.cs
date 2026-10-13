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
    private readonly int port;

    public event Action<DiscoveredPhone>? PhoneDiscovered;
    public Func<WifiPairRequest, Task<Settings>>? PairRequested { get; set; }
    public Action<string, JsonElement>? PairedTelemetry { get; set; }
    private WifiPairingTicket? invitation;
    public WifiPairingTicket OpenPairing() => invitation = new WifiPairingTicket();
    public void ClosePairing(WifiPairingTicket ticket) { if (ReferenceEquals(invitation, ticket)) invitation = null; }
    public async Task StopPairedAsync(string token) {
        var ticket = invitation;
        if (ticket == null || ticket.Token != token) return;
        ticket.StopRequested = true;
        try { await ticket.StopDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
    }

    public WifiDiscoveryService(Func<Settings> getSettings, Action<string> log, int port = DiscoveryPort) {
        this.getSettings = getSettings;
        this.log = log;
        this.port = port;
    }

    public void Start() {
        if (disposed || listenTask != null) return;
        try {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
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
            var candidates = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                .OrderBy(ni => new[] { "virtual", "vpn", "xray", "wireguard", "tap", "tunnel", "usb", "rndis", "remote ndis" }
                    .Any(word => ni.Description.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
                .ThenBy(ni => ni.GetIPProperties().GatewayAddresses.Count > 0 ? 0 : 1);
            foreach (var ni in candidates) {
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
                if (text.StartsWith("H3HCAM_CONTROL_POLL ", StringComparison.Ordinal)) {
                    using var poll = JsonDocument.Parse(text[20..]);
                    var ticket = invitation;
                    var token = poll.RootElement.GetProperty("token").GetString();
                    if (ticket?.ReadySettings == null || ticket.Token != token || ticket.PhoneIp != result.RemoteEndPoint.Address.ToString()) continue;
                    if (poll.RootElement.TryGetProperty("telemetry", out var telemetry)) PairedTelemetry?.Invoke(ticket.PhoneIp, telemetry);
                    var current = getSettings();
                    object command = ticket.StopRequested || current.WifiPairSession != token
                        ? new { command = "STOP_STREAM" } : WifiPairing.StreamCommand(current, "SET_CONTROLS");
                    var serialized = JsonSerializer.Serialize(command);
                    var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized)))[..16];
                    var previous = poll.RootElement.TryGetProperty("revision", out var rev) ? rev.GetString() : "";
                    if (previous == revision && !ticket.StopRequested) command = new { command = "HEARTBEAT" };
                    var response = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(command))!;
                    response["session"] = token;
                    response["revision"] = revision;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                    await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                    if (ticket.StopRequested) ticket.StopDelivered.TrySetResult();
                    continue;
                }
                if (text.StartsWith("H3HCAM_PAIR ", StringComparison.Ordinal)) {
                    using var doc = JsonDocument.Parse(text[12..]);
                    var token = doc.RootElement.GetProperty("token").GetString() ?? "";
                    var ticket = invitation;
                    if (ticket == null || !ticket.Claim(token, result.RemoteEndPoint.Address) || PairRequested == null) continue;
                    object reply;
                    try {
                        var phone = PhoneCapabilities.ParseJson(doc.RootElement.GetProperty("capabilities").GetRawText());
                        if (phone.Cameras.Count == 0) throw new InvalidOperationException(L.Get("setup_no_cameras"));
                        var ready = ticket.ReadySettings ?? await PairRequested(new(result.RemoteEndPoint.Address.ToString(), token, phone));
                        ticket.ReadySettings = ready;
                        reply = new { token, ready = true, port = ready.RtpPort, settings = WifiPairing.StreamCommand(ready) };
                    } catch (Exception ex) {
                        log("Wi-Fi pairing: " + ex.Message);
                        reply = new { token, ready = false, error = ex.Message };
                    }
                    var bytes = Encoding.UTF8.GetBytes("H3HCAM_PAIR_READY " + JsonSerializer.Serialize(reply));
                    await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                    continue;
                }
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
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, port);
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
