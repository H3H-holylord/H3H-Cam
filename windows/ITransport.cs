namespace S8Cam;

public enum TransportState {
    Disconnected,
    Discovering,
    Connecting,
    Connected,
    Recovering,
    Failed
}

public sealed record TransportStats(
    string Name,
    TransportState State,
    long BytesReceived,
    long PacketsReceived,
    double BitrateMbps,
    double LatencyMs,
    long LostPackets,
    long RecoveredPackets,
    int ReconnectCount,
    string Details
);

public interface ITransport : IAsyncDisposable {
    string Name { get; }
    TransportState State { get; }
    Task<bool> ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task SendControlAsync(H3HMessage message, CancellationToken ct);
    ValueTask<H3HMessage?> ReceiveMessageAsync(CancellationToken ct);
    TransportStats GetStats();
}
