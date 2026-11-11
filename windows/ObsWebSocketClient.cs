using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace S8Cam;

public sealed record ObsLiveState(
    bool Connected,
    string CurrentScene,
    bool Streaming,
    bool Recording,
    bool SceneMatchesCamera,
    bool TallyOnAir,
    string StatusText);

/// <summary>
/// Асинхронный клиент протокола OBS WebSocket v5 (порт 4455 по умолчанию):
/// - Авторизация SHA256 challenge/salt
/// - Живой Tally-мониторинг («В ЭФИРЕ» по текущей Program-сцене и статусу стрима/записи)
/// - Управление Эко-паузой камеры при уходе сцены из эфира
/// - Автосинхронизация задержки звука (SetInputAudioSyncOffset) в 1 клик
/// </summary>
public sealed class ObsWebSocketClient : IAsyncDisposable {
    private readonly Action<string> log;
    private CancellationTokenSource? cts;
    private Task? workerTask;
    private ClientWebSocket? activeSocket;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    private volatile string url = "ws://127.0.0.1:4455";
    private volatile string password = "";
    private volatile string targetScene = "";
    private volatile bool connected;
    private volatile string currentScene = "";
    private volatile bool streaming;
    private volatile bool recording;

    public event Action<ObsLiveState>? StateChanged;

    public ObsLiveState CurrentState {
        get {
            bool isConn = connected;
            string scene = currentScene;
            bool isStream = streaming;
            bool isRec = recording;
            string target = targetScene;
            bool matches = string.IsNullOrWhiteSpace(target)
                ? true
                : (!string.IsNullOrEmpty(scene) && scene.Contains(target, StringComparison.OrdinalIgnoreCase));
            bool onAir = isConn && matches && (isStream || isRec || !string.IsNullOrWhiteSpace(target));
            string status = !isConn
                ? "OBS не подключён"
                : $"Сцена: {(string.IsNullOrEmpty(scene) ? "—" : scene)}" +
                  (isStream ? " · ЭФИР" : "") +
                  (isRec ? " · REC" : "");
            return new ObsLiveState(isConn, scene, isStream, isRec, matches, onAir, status);
        }
    }

    public ObsWebSocketClient(Action<string> log) {
        this.log = log;
    }

    public void ConfigureAndStart(string wsUrl, string wsPassword, string cameraSceneName, bool enabled) {
        url = string.IsNullOrWhiteSpace(wsUrl) ? "ws://127.0.0.1:4455" : wsUrl.Trim();
        password = wsPassword ?? "";
        targetScene = cameraSceneName?.Trim() ?? "";

        if (!enabled) {
            StopSync();
            PublishState();
            return;
        }

        if (workerTask is { IsCompleted: false }) {
            PublishState();
            return;
        }

        StopSync();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        workerTask = Task.Run(() => ConnectionLoop(token), token);
    }

    public void UpdateTargetScene(string cameraSceneName) {
        targetScene = cameraSceneName?.Trim() ?? "";
        PublishState();
    }

    public static string ComputeObsAuthResponse(string password, string salt, string challenge) {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        string secretBase64 = Convert.ToBase64String(secretHash);
        byte[] authHash = SHA256.HashData(Encoding.UTF8.GetBytes(secretBase64 + challenge));
        return Convert.ToBase64String(authHash);
    }

    public async Task<bool> SetAudioSyncOffsetAsync(string inputName, int offsetMs, CancellationToken ct = default) {
        var ws = activeSocket;
        if (!connected || ws == null || ws.State != WebSocketState.Open || string.IsNullOrWhiteSpace(inputName)) {
            return false;
        }
        int clampedMs = Math.Clamp(offsetMs, -950, 20000);
        var payload = new {
            op = 6,
            d = new {
                requestType = "SetInputAudioSyncOffset",
                requestId = "sync_" + Guid.NewGuid().ToString("N")[..8],
                requestData = new {
                    inputName = inputName.Trim(),
                    inputAudioSyncOffset = clampedMs
                }
            }
        };
        try {
            await SendJsonAsync(ws, payload, ct);
            log($"OBS Lip-Sync · установлена задержка {clampedMs} мс для аудиовхода «{inputName.Trim()}»");
            return true;
        } catch (Exception ex) {
            log("OBS Lip-Sync ошибка: " + ex.Message);
            return false;
        }
    }

    private async Task ConnectionLoop(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            using var ws = new ClientWebSocket();
            activeSocket = ws;
            try {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
                    log("OBS WebSocket: некорректный URL " + url);
                    await Task.Delay(5000, ct);
                    continue;
                }

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                await ws.ConnectAsync(uri, connectTimeout.Token);

                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open) {
                    string? json = await ReceiveMessageAsync(ws, ct);
                    if (json == null) break;
                    await HandleMessageAsync(ws, json, ct);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch {
                // OBS может быть закрыт — тихо ждём следующей попытки переподключения
            } finally {
                if (connected) {
                    connected = false;
                    streaming = false;
                    recording = false;
                    PublishState();
                }
                if (ReferenceEquals(activeSocket, ws)) activeSocket = null;
            }

            try {
                await Task.Delay(3500, ct);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    private async Task HandleMessageAsync(ClientWebSocket ws, string json, CancellationToken ct) {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("op", out var opProp)) return;
        int op = opProp.GetInt32();
        if (!root.TryGetProperty("d", out var d)) return;

        switch (op) {
            case 0: { // Hello
                string? authResponse = null;
                if (d.TryGetProperty("authentication", out var authObj) && authObj.ValueKind == JsonValueKind.Object) {
                    string challenge = authObj.TryGetProperty("challenge", out var ch) ? ch.GetString() ?? "" : "";
                    string salt = authObj.TryGetProperty("salt", out var sl) ? sl.GetString() ?? "" : "";
                    authResponse = ComputeObsAuthResponse(password, salt, challenge);
                }

                object identify = authResponse != null
                    ? new { op = 1, d = new { rpcVersion = 1, authentication = authResponse, eventSubscriptions = 69 } }
                    : new { op = 1, d = new { rpcVersion = 1, eventSubscriptions = 69 } };

                await SendJsonAsync(ws, identify, ct);
                break;
            }
            case 2: { // Identified
                connected = true;
                log("OBS WebSocket v5 подключён (" + url + ")");
                await SendJsonAsync(ws, new { op = 6, d = new { requestType = "GetCurrentProgramScene", requestId = "init_scene" } }, ct);
                await SendJsonAsync(ws, new { op = 6, d = new { requestType = "GetStreamStatus", requestId = "init_stream" } }, ct);
                await SendJsonAsync(ws, new { op = 6, d = new { requestType = "GetRecordStatus", requestId = "init_record" } }, ct);
                PublishState();
                break;
            }
            case 5: { // Event
                string eventType = d.TryGetProperty("eventType", out var et) ? et.GetString() ?? "" : "";
                if (!d.TryGetProperty("eventData", out var ed) || ed.ValueKind != JsonValueKind.Object) break;

                if (eventType == "CurrentProgramSceneChanged") {
                    currentScene = ed.TryGetProperty("sceneName", out var sn) ? sn.GetString() ?? "" : currentScene;
                    PublishState();
                } else if (eventType == "StreamStateChanged") {
                    if (ed.TryGetProperty("outputActive", out var oa) && oa.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                        streaming = oa.GetBoolean();
                        PublishState();
                    }
                } else if (eventType == "RecordStateChanged") {
                    if (ed.TryGetProperty("outputActive", out var oa) && oa.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                        recording = oa.GetBoolean();
                        PublishState();
                    }
                }
                break;
            }
            case 7: { // RequestResponse
                string reqType = d.TryGetProperty("requestType", out var rt) ? rt.GetString() ?? "" : "";
                if (!d.TryGetProperty("responseData", out var rd) || rd.ValueKind != JsonValueKind.Object) break;

                if (reqType == "GetCurrentProgramScene") {
                    currentScene = rd.TryGetProperty("currentProgramSceneName", out var sn)
                        ? sn.GetString() ?? ""
                        : (rd.TryGetProperty("sceneName", out var sn2) ? sn2.GetString() ?? "" : currentScene);
                    PublishState();
                } else if (reqType == "GetStreamStatus") {
                    if (rd.TryGetProperty("outputActive", out var oa) && oa.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                        streaming = oa.GetBoolean();
                        PublishState();
                    }
                } else if (reqType == "GetRecordStatus") {
                    if (rd.TryGetProperty("outputActive", out var oa) && oa.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                        recording = oa.GetBoolean();
                        PublishState();
                    }
                }
                break;
            }
        }
    }

    private async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct) {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await sendLock.WaitAsync(ct);
        try {
            if (ws.State == WebSocketState.Open) {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        } finally {
            sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct) {
        var buffer = new byte[8192];
        using var ms = new System.IO.MemoryStream();
        while (true) {
            var res = await ws.ReceiveAsync(buffer, ct);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage) break;
            if (ms.Length > 512 * 1024) return null;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private void PublishState() {
        try {
            StateChanged?.Invoke(CurrentState);
        } catch { }
    }

    private void StopSync() {
        var oldCts = cts;
        cts = null;
        oldCts?.Cancel();
        try { activeSocket?.Abort(); } catch { }
        activeSocket = null;
        connected = false;
        streaming = false;
        recording = false;
        oldCts?.Dispose();
    }

    public async ValueTask DisposeAsync() {
        var task = workerTask;
        StopSync();
        if (task != null) {
            try { await task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        sendLock.Dispose();
    }
}
