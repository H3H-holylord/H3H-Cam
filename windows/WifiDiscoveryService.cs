using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace S8Cam;

public sealed record DiscoveredPhone(string Ip, string Model, DateTime Time, int PhonePort = WifiDiscoveryService.DefaultPhoneBeaconPort);

public sealed record LocalLanAdapter(string Ip, string BroadcastIp, string Label, bool IsVirtualOrUsb);

/// <summary>
/// Беспроводной сервис обнаружения и сопряжения ПК ↔ смартфон (UDP + TCP).
/// Поддерживает ПК с несколькими сетевыми адаптерами (например, домашний роутер + USB-модем первого смартфона),
/// инстансы мультикама, TCP-фоллбэк для QR-кода (в обход блокировок UDP/VPN) и прямое обнаружение/запуск второго телефона по Wi-Fi без ADB.
/// </summary>
public sealed class WifiDiscoveryService : IDisposable {
    public const int DiscoveryPort = 5005;
    public const int DefaultPhoneBeaconPort = 5006;
    public static int DefaultPortForInstance => DiscoveryPort + (Math.Max(1, Settings.ActiveInstance) - 1) * 10;

    private readonly Func<Settings> getSettings;
    private readonly Action<string> log;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<string, DiscoveredPhone> discoveredPhones = new(StringComparer.OrdinalIgnoreCase);
    private UdpClient? udp;
    private TcpListener? tcp;
    private Task? listenTask;
    private Task? tcpListenTask;
    private Task? broadcastTask;
    private bool disposed;
    private readonly int port;

    public int Port => port;
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

    public WifiDiscoveryService(Func<Settings> getSettings, Action<string> log, int port = 0) {
        this.getSettings = getSettings;
        this.log = log;
        this.port = port > 0 ? port : DefaultPortForInstance;
    }

    public IReadOnlyList<DiscoveredPhone> GetDiscoveredPhones() {
        var cutoff = DateTime.Now.AddMinutes(-5);
        return discoveredPhones.Values
            .Where(p => p.Time >= cutoff)
            .OrderByDescending(p => p.Time)
            .ToList();
    }

    public void Start() {
        if (disposed) return;
        if (listenTask == null) {
            try {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                udp.EnableBroadcast = true;
                listenTask = ListenLoop(cts.Token);
                broadcastTask = BroadcastLoop(cts.Token);
            } catch (Exception ex) {
                log("Wi-Fi UDP Discovery bind: " + ex.Message);
            }
        }
        if (tcpListenTask == null) {
            try {
                tcp = new TcpListener(IPAddress.Any, port);
                tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                tcp.Start();
                tcpListenTask = TcpListenLoop(cts.Token);
            } catch (Exception ex) {
                log("Wi-Fi TCP Pairing bind: " + ex.Message);
            }
        }
    }

    private static bool IsPrivateLanIpv4(IPAddress addr) {
        if (addr.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(addr)) return false;
        var b = addr.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }

    private static string ComputeBroadcastAddress(IPAddress ip, IPAddress? mask) {
        if (ip.AddressFamily != AddressFamily.InterNetwork || mask == null || mask.AddressFamily != AddressFamily.InterNetwork)
            return IPAddress.Broadcast.ToString();
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (maskBytes.All(x => x == 0)) return IPAddress.Broadcast.ToString();
        var bcast = new byte[4];
        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        return new IPAddress(bcast).ToString();
    }

    public static List<LocalLanAdapter> GetAllLocalLanAdapters() {
        var result = new List<(LocalLanAdapter Item, bool HasGateway, int TypeRank)>();
        var virtualWords = new[] {
            "virtual", "vpn", "xray", "wireguard", "tap", "tunnel", "usb", "rndis",
            "remote ndis", "bluetooth", "hyper-v", "wsl", "vmware", "vbox", "loopback"
        };

        try {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var desc = $"{ni.Name} {ni.Description}";
                bool isVirtualOrUsb = virtualWords.Any(w => desc.Contains(w, StringComparison.OrdinalIgnoreCase));
                int typeRank = ni.NetworkInterfaceType switch {
                    NetworkInterfaceType.Ethernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2
                };

                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

                foreach (var uni in props.UnicastAddresses) {
                    if (!IsPrivateLanIpv4(uni.Address)) continue;
                    var ipStr = uni.Address.ToString();
                    if (result.Any(x => x.Item.Ip == ipStr)) continue;
                    var bcast = ComputeBroadcastAddress(uni.Address, uni.IPv4Mask);
                    var cleanName = string.IsNullOrWhiteSpace(ni.Description) ? ni.Name : ni.Description;
                    var tag = isVirtualOrUsb ? "USB/VPN" : (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "LAN");
                    result.Add((new LocalLanAdapter(ipStr, bcast, $"{ipStr} ({tag} · {cleanName})", isVirtualOrUsb), hasGateway, typeRank));
                }
            }
        } catch { }

        return result
            .OrderBy(x => x.Item.IsVirtualOrUsb ? 1 : 0)
            .ThenBy(x => x.HasGateway ? 0 : 1)
            .ThenBy(x => x.TypeRank)
            .Select(x => x.Item)
            .ToList();
    }

    public static List<string> GetAllLocalLanIpv4() =>
        GetAllLocalLanAdapters().Select(a => a.Ip).ToList();

    public static string ResolveBestLocalIpv4(IPAddress? remote = null) {
        if (remote != null && !IPAddress.IsLoopback(remote) && !remote.Equals(IPAddress.Any) && !remote.Equals(IPAddress.Broadcast)) {
            // Сначала ищем локальный адаптер в той же подсети /24, что и телефон
            var rb = remote.GetAddressBytes();
            foreach (var adapter in GetAllLocalLanAdapters()) {
                if (IPAddress.TryParse(adapter.Ip, out var lip)) {
                    var lb = lip.GetAddressBytes();
                    if (lb[0] == rb[0] && lb[1] == rb[1] && lb[2] == rb[2]) return adapter.Ip;
                }
            }
            try {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(remote, 65530);
                if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address) && !ep.Address.Equals(IPAddress.Any)) {
                    return ep.Address.ToString();
                }
            } catch { }
        }

        var adapters = GetAllLocalLanAdapters();
        if (adapters.Count > 0) return adapters[0].Ip;
        return AdbController.FindLocalIpv4();
    }

    private DiscoveredPhone RegisterDiscoveredPhone(IPAddress remoteAddress, string? rawJsonPayload = null) {
        var phoneIp = remoteAddress.ToString();
        var phoneModel = "Android Phone";
        var phonePort = DefaultPhoneBeaconPort;
        if (!string.IsNullOrWhiteSpace(rawJsonPayload)) {
            try {
                using var doc = JsonDocument.Parse(rawJsonPayload);
                var root = doc.RootElement;
                var m = root.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
                var mf = root.TryGetProperty("manufacturer", out var mfEl) ? mfEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(m)) {
                    phoneModel = !string.IsNullOrWhiteSpace(mf) && !m.StartsWith(mf, StringComparison.OrdinalIgnoreCase)
                        ? $"{mf} {m}"
                        : m;
                }
                if (root.TryGetProperty("phone_port", out var pEl) && pEl.TryGetInt32(out var pp) && pp is > 0 and <= 65535) {
                    phonePort = pp;
                }
            } catch { }
        }

        var phone = new DiscoveredPhone(phoneIp, phoneModel, DateTime.Now, phonePort);
        var isNew = !discoveredPhones.TryGetValue(phoneIp, out var prev) || (DateTime.Now - prev.Time).TotalSeconds > 30;
        discoveredPhones[phoneIp] = phone;
        if (isNew) PhoneDiscovered?.Invoke(phone);
        return phone;
    }

    private byte[]? BuildControlPollResponse(string jsonPayload, IPAddress remoteAddress) {
        using var poll = JsonDocument.Parse(jsonPayload);
        var ticket = invitation;
        var token = poll.RootElement.GetProperty("token").GetString();
        if (ticket?.ReadySettings == null || ticket.Token != token || ticket.PhoneIp != remoteAddress.ToString())
            return null;
        if (poll.RootElement.TryGetProperty("telemetry", out var telemetry))
            PairedTelemetry?.Invoke(ticket.PhoneIp, telemetry);

        var current = getSettings();
        var gracePeriod = (DateTimeOffset.UtcNow - (ticket.Expires.AddMinutes(-5))).TotalSeconds < 6.0;
        var effectiveSettings = (!string.IsNullOrEmpty(current.WifiPairSession) || !gracePeriod) ? current : ticket.ReadySettings;
        object command = ticket.StopRequested || effectiveSettings.WifiPairSession != token
            ? new { command = "STOP_STREAM" }
            : WifiPairing.StreamCommand(effectiveSettings, "SET_CONTROLS");
        var serialized = JsonSerializer.Serialize(command);
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(serialized)))[..16];
        var previous = poll.RootElement.TryGetProperty("revision", out var rev) ? rev.GetString() : "";
        if (previous == revision && !ticket.StopRequested) command = new { command = "HEARTBEAT" };
        var response = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(command))!;
        response["session"] = token;
        response["revision"] = revision;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        if (ticket.StopRequested) ticket.StopDelivered.TrySetResult();
        return bytes;
    }

    private async Task<string?> BuildPairReadyResponseAsync(string jsonPayload, IPAddress remoteAddress) {
        using var doc = JsonDocument.Parse(jsonPayload);
        var token = doc.RootElement.GetProperty("token").GetString() ?? "";
        var ticket = invitation;
        if (ticket == null || !ticket.Claim(token, remoteAddress) || PairRequested == null)
            return null;
        object reply;
        try {
            var phone = PhoneCapabilities.ParseJson(doc.RootElement.GetProperty("capabilities").GetRawText());
            if (phone.Cameras.Count == 0) throw new InvalidOperationException(L.Get("setup_no_cameras"));
            RegisterDiscoveredPhone(remoteAddress, JsonSerializer.Serialize(new { model = phone.Model, manufacturer = phone.Manufacturer }));
            var ready = ticket.ReadySettings ?? await PairRequested(new(remoteAddress.ToString(), token, phone));
            ticket.ReadySettings = ready;
            reply = new { token, ready = true, port = ready.RtpPort, dp = port, settings = WifiPairing.StreamCommand(ready) };
        } catch (Exception ex) {
            log("Wi-Fi pairing: " + ex.Message);
            reply = new { token, ready = false, error = ex.Message };
        }
        return "H3HCAM_PAIR_READY " + JsonSerializer.Serialize(reply);
    }

    private string BuildPongResponse(IPAddress remoteAddress) {
        var s = getSettings();
        var pcIp = (!s.AutoPcIp && !string.IsNullOrWhiteSpace(s.PcIp) && s.PcIp != "127.0.0.1" && s.PcIp != "0.0.0.0")
            ? s.PcIp
            : ResolveBestLocalIpv4(remoteAddress);
        var replyJson = JsonSerializer.Serialize(new {
            service = "h3hcam",
            pc_name = Environment.MachineName,
            pc_ip = pcIp,
            port = s.RtpPort,
            dp = port,
            codec = s.Codec,
            fps = s.Fps
        });
        return "H3HCAM_PONG " + replyJson;
    }

    private async Task ListenLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested && udp != null) {
            try {
                var result = await udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith("H3HCAM_CONTROL_POLL ", StringComparison.Ordinal)) {
                    var bytes = BuildControlPollResponse(text[20..], result.RemoteEndPoint.Address);
                    if (bytes != null) await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                    continue;
                }
                if (text.StartsWith("H3HCAM_PAIR ", StringComparison.Ordinal)) {
                    var pairReply = await BuildPairReadyResponseAsync(text[12..], result.RemoteEndPoint.Address);
                    if (pairReply != null) {
                        var bytes = Encoding.UTF8.GetBytes(pairReply);
                        await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                    }
                    continue;
                }
                if (text.Contains("H3HCAM_DISCOVERY_PING", StringComparison.Ordinal) || text.Contains("\"action\":\"ping\"", StringComparison.Ordinal)) {
                    var rawJson = text.StartsWith("H3HCAM_DISCOVERY_PING ", StringComparison.Ordinal) ? text[22..] : text;
                    RegisterDiscoveredPhone(result.RemoteEndPoint.Address, rawJson);
                    var replyBytes = Encoding.UTF8.GetBytes(BuildPongResponse(result.RemoteEndPoint.Address));
                    await udp.SendAsync(replyBytes, replyBytes.Length, result.RemoteEndPoint);
                }
            } catch (OperationCanceledException) { break; }
            catch (Exception) { /* игнорируем случайный сетевой шум */ }
        }
    }

    private async Task TcpListenLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested && tcp != null) {
            TcpClient client;
            try {
                client = await tcp.AcceptTcpClientAsync(ct);
            } catch (OperationCanceledException) { break; }
            catch (Exception) { if (ct.IsCancellationRequested) break; continue; }

            _ = Task.Run(async () => {
                using (client) {
                    try {
                        client.NoDelay = true;
                        if (client.Client.RemoteEndPoint is not IPEndPoint remoteEp) return;
                        var remoteIp = remoteEp.Address.IsIPv4MappedToIPv6 ? remoteEp.Address.MapToIPv4() : remoteEp.Address;
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                        var line = await reader.ReadLineAsync(timeoutCts.Token);
                        if (string.IsNullOrWhiteSpace(line)) return;

                        if (line.StartsWith("H3HCAM_PAIR ", StringComparison.Ordinal)) {
                            var pairReply = await BuildPairReadyResponseAsync(line[12..], remoteIp);
                            if (pairReply != null) {
                                var bytes = Encoding.UTF8.GetBytes(pairReply + "\n");
                                await stream.WriteAsync(bytes, timeoutCts.Token);
                                await stream.FlushAsync(timeoutCts.Token);
                            }
                        } else if (line.StartsWith("H3HCAM_CONTROL_POLL ", StringComparison.Ordinal)) {
                            var bytes = BuildControlPollResponse(line[20..], remoteIp);
                            if (bytes != null) {
                                await stream.WriteAsync(bytes, timeoutCts.Token);
                                await stream.WriteAsync("\n"u8.ToArray(), timeoutCts.Token);
                                await stream.FlushAsync(timeoutCts.Token);
                            }
                        } else if (line.StartsWith("H3HCAM_DISCOVERY_PING", StringComparison.Ordinal)) {
                            var rawJson = line.StartsWith("H3HCAM_DISCOVERY_PING ", StringComparison.Ordinal) ? line[22..] : "";
                            RegisterDiscoveredPhone(remoteIp, rawJson);
                            var bytes = Encoding.UTF8.GetBytes(BuildPongResponse(remoteIp) + "\n");
                            await stream.WriteAsync(bytes, timeoutCts.Token);
                            await stream.FlushAsync(timeoutCts.Token);
                        }
                    } catch { }
                }
            }, ct);
        }
    }

    private async Task BroadcastLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested && udp != null) {
            try {
                var s = getSettings();
                var adapters = GetAllLocalLanAdapters();
                if (adapters.Count == 0) {
                    var fallbackIp = ResolveBestLocalIpv4();
                    if (!string.IsNullOrWhiteSpace(fallbackIp) && fallbackIp != "127.0.0.1") {
                        var bytes = BuildBeaconPacket(s, fallbackIp);
                        await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, port));
                    }
                } else {
                    foreach (var adapter in adapters) {
                        var advertisedIp = (!s.AutoPcIp && !string.IsNullOrWhiteSpace(s.PcIp) && s.PcIp != "127.0.0.1")
                            ? s.PcIp
                            : adapter.Ip;
                        var bytes = BuildBeaconPacket(s, advertisedIp);
                        if (IPAddress.TryParse(adapter.BroadcastIp, out var bcastAddr)) {
                            try { await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(bcastAddr, port)); } catch { }
                        }
                    }
                }
            } catch { }
            await Task.Delay(2000, ct);
        }
    }

    private byte[] BuildBeaconPacket(Settings s, string pcIp) {
        var beacon = JsonSerializer.Serialize(new {
            service = "h3hcam",
            pc_name = Environment.MachineName,
            pc_ip = pcIp,
            port = s.RtpPort,
            dp = port,
            codec = s.Codec,
            fps = s.Fps
        });
        return Encoding.UTF8.GetBytes("H3HCAM_BEACON " + beacon);
    }

    /// <summary>
    /// Активно опрашивает все локальные сети ПК (включая домашний роутер 192.168.1.x даже при включённом USB-модеме первого смартфона)
    /// и возвращает список найденных смартфонов с открытым приложением H3H Cam.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPhone>> ProbeLanPhonesAsync(CancellationToken ct = default) {
        var scanBytes = Encoding.UTF8.GetBytes($"H3HCAM_PC_SCAN {{\"pc_name\":\"{Environment.MachineName}\",\"dp\":{port}}}");
        var adapters = GetAllLocalLanAdapters();
        var probeSockets = new List<UdpClient>();

        try {
            if (adapters.Count == 0) {
                var sock = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                probeSockets.Add(sock);
            } else {
                foreach (var adapter in adapters) {
                    if (!IPAddress.TryParse(adapter.Ip, out var localAddr)) continue;
                    try {
                        var sock = new UdpClient(new IPEndPoint(localAddr, 0)) { EnableBroadcast = true };
                        probeSockets.Add(sock);
                    } catch { }
                }
            }

            var receiveTasks = probeSockets.Select(sock => Task.Run(async () => {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromMilliseconds(1250));
                while (!waitCts.IsCancellationRequested) {
                    try {
                        var pkt = await sock.ReceiveAsync(waitCts.Token);
                        var text = Encoding.UTF8.GetString(pkt.Buffer);
                        if (text.StartsWith("H3HCAM_DISCOVERY_PING", StringComparison.Ordinal)) {
                            var rawJson = text.Length > 22 ? text[22..] : "";
                            RegisterDiscoveredPhone(pkt.RemoteEndPoint.Address, rawJson);
                        }
                    } catch { break; }
                }
            }, ct)).ToList();

            for (int wave = 0; wave < 2 && !ct.IsCancellationRequested; wave++) {
                for (int i = 0; i < probeSockets.Count; i++) {
                    var sock = probeSockets[i];
                    var bcastStr = i < adapters.Count ? adapters[i].BroadcastIp : IPAddress.Broadcast.ToString();
                    if (IPAddress.TryParse(bcastStr, out var bcastIp)) {
                        try { await sock.SendAsync(scanBytes, scanBytes.Length, new IPEndPoint(bcastIp, DefaultPhoneBeaconPort)); } catch { }
                    }
                    try { await sock.SendAsync(scanBytes, scanBytes.Length, new IPEndPoint(IPAddress.Broadcast, DefaultPhoneBeaconPort)); } catch { }
                }
                if (wave == 0) await Task.Delay(350, ct);
            }

            await Task.WhenAll(receiveTasks);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
        } finally {
            foreach (var s in probeSockets) {
                try { s.Dispose(); } catch { }
            }
        }

        return GetDiscoveredPhones();
    }

    /// <summary>
    /// Напрямую запрашивает список камер второго смартфона по Wi-Fi (TCP порт 5006) без кабеля и без ADB.
    /// </summary>
    public async Task<PhoneCapabilities?> FetchPhoneCapabilitiesAsync(string phoneIp, int phonePort = DefaultPhoneBeaconPort, CancellationToken ct = default) {
        var cleanHost = Settings.EndpointHost(phoneIp);
        if (!IPAddress.TryParse(cleanHost, out var ipAddr)) return null;

        try {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(2400));
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(ipAddr, phonePort, timeoutCts.Token);
            var stream = client.GetStream();
            var req = "H3HCAM_GET_CAPS\n"u8.ToArray();
            await stream.WriteAsync(req, timeoutCts.Token);
            await stream.FlushAsync(timeoutCts.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line != null && line.StartsWith("H3HCAM_CAPS ", StringComparison.Ordinal)) {
                var caps = PhoneCapabilities.ParseJson(line[12..]);
                RegisterDiscoveredPhone(ipAddr, JsonSerializer.Serialize(new {
                    model = caps.Model,
                    manufacturer = caps.Manufacturer,
                    phone_port = phonePort
                }));
                return caps;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch { }
        return null;
    }

    /// <summary>
    /// Запускает передачу видео/микрофона на смартфоне напрямую по Wi-Fi (TCP порт 5006) по нажатию «СТАРТ» на ПК.
    /// Возвращает токен сессии для ReceiverEngine (PairedWifi).
    /// </summary>
    public async Task<string?> StartPhoneWifiStreamAsync(string phoneIp, Settings readySettings, int phonePort = DefaultPhoneBeaconPort, CancellationToken ct = default) {
        var cleanHost = Settings.EndpointHost(phoneIp);
        if (!IPAddress.TryParse(cleanHost, out var ipAddr)) return null;

        Start();
        var ticket = OpenPairing();
        if (!ticket.Claim(ticket.Token, ipAddr)) return null;

        var pcIp = (!readySettings.AutoPcIp && !string.IsNullOrWhiteSpace(readySettings.PcIp) && readySettings.PcIp != "127.0.0.1")
            ? readySettings.PcIp
            : ResolveBestLocalIpv4(ipAddr);

        var sessionSettings = readySettings.Clone();
        sessionSettings.PcIp = pcIp;
        sessionSettings.PhoneIp = cleanHost;
        sessionSettings.WifiPairSession = ticket.Token;
        ticket.ReadySettings = sessionSettings;

        try {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(ipAddr, phonePort, timeoutCts.Token);
            var stream = client.GetStream();

            var payload = JsonSerializer.Serialize(new {
                pc_ip = pcIp,
                port = sessionSettings.RtpPort,
                token = ticket.Token,
                dp = port,
                settings = WifiPairing.StreamCommand(sessionSettings)
            });
            var reqBytes = Encoding.UTF8.GetBytes("H3HCAM_WIFI_START " + payload + "\n");
            await stream.WriteAsync(reqBytes, timeoutCts.Token);
            await stream.FlushAsync(timeoutCts.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line != null && line.StartsWith("H3HCAM_WIFI_STARTED", StringComparison.Ordinal)) {
                return ticket.Token;
            }
            if (line != null && line.StartsWith("H3HCAM_START_ERR ", StringComparison.Ordinal)) {
                log("Wi-Fi Start: " + line[17..]);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            ClosePairing(ticket);
            throw;
        } catch {
            ClosePairing(ticket);
        }
        return null;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { tcp?.Stop(); } catch { }
        tcp = null;
        udp?.Dispose();
        udp = null;
        cts.Dispose();
    }
}
