namespace S8Cam;

public sealed class ConnectionManager(Action<string> log) {
    public TransportState State { get; private set; } = TransportState.Disconnected;
    public string ActiveTransport { get; private set; } = "";
    public int ReconnectAttempts { get; private set; }

    public event Action<TransportState, string>? StateChanged;

    private void SetState(TransportState nextState, string details) {
        if (State == nextState && ActiveTransport == details) return;
        State = nextState;
        ActiveTransport = details;
        log($"ConnectionManager · [{nextState}] {details}");
        StateChanged?.Invoke(nextState, details);
    }

    public async Task<string> ResolveAutoTransportAsync(
        AoaController? aoaProbe,
        AdbController adb,
        Settings settings,
        CancellationToken ct) {
        
        SetState(TransportState.Discovering, "Поиск лучшего подключения (USB Direct → ADB → Wi-Fi)...");

        // 1. Попытка USB Direct (AOA)
        try {
            using var direct = new AoaController();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(4000));
            // AUTO must not switch a working USB/ADB device into accessory mode while probing.
            var directProbe = await direct.ConnectAsync(timeoutCts.Token, allowSwitch: false);
            if (directProbe.Ready) {
                SetState(TransportState.Connecting, "Обнаружен USB Direct (AOA)");
                return "direct";
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        // 2. Попытка USB ADB (физический USB-кабель с включенной отладкой)
        try {
            await adb.RefreshDevices(ct);
            var physicalUsb = adb.Devices.FirstOrDefault(d => !d.IsNetwork && d.Authorized);
            if (physicalUsb != null) {
                SetState(TransportState.Connecting, $"Обнаружен телефон по USB ADB: {physicalUsb.Display}");
                return "usb";
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        ct.ThrowIfCancellationRequested();

        // 3. Попытка Wi-Fi (если задан IP или обнаружен в LAN)
        if (!string.IsNullOrWhiteSpace(settings.PhoneIp) && settings.PhoneIp != "127.0.0.1") {
            SetState(TransportState.Connecting, $"Используется локальная сеть Wi-Fi: {settings.PhoneIp}");
            return "wifi";
        }

        SetState(TransportState.Failed, "Устройства не обнаружены ни по USB, ни по Wi-Fi");
        return "usb"; // Fallback to USB ADB for descriptive error
    }

    public void OnConnected(string transport) {
        ReconnectAttempts = 0;
        SetState(TransportState.Connected, transport);
    }

    public void OnDisconnected(string reason) {
        SetState(TransportState.Disconnected, reason);
    }

    public void OnRecovering(string transport, int attempt, int delayMs) {
        ReconnectAttempts = attempt;
        SetState(TransportState.Recovering, $"Восстановление {transport} (попытка {attempt}, пауза {delayMs} мс)");
    }

    public static int CalculateBackoffMs(int attempt, int minMs = 800, int maxMs = 8000) {
        var shift = Math.Min(attempt, 4);
        var delay = minMs * (1 << shift);
        return Math.Min(delay, maxMs);
    }
}
