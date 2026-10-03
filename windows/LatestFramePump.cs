using System.IO;
using System.Threading.Channels;

namespace S8Cam;

/// <summary>Drains complete decoded frames independently of a slower video output.</summary>
public static class LatestFramePump {
    public static async Task RunAsync(Stream input, int frameBytes, Action<byte[]> output,
        CancellationToken cancellation, Action? onDropped = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // Exactly three buffers: reading, pending, and in use by the output.
        var free = new System.Collections.Concurrent.ConcurrentQueue<byte[]>(
            Enumerable.Range(0, 3).Select(_ => new byte[frameBytes]));
        var latest = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) {
            SingleReader = true, SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        }, dropped => { free.Enqueue(dropped); onDropped?.Invoke(); });

        var consumer = Task.Run(async () => {
            try {
                await foreach (var frame in latest.Reader.ReadAllAsync(stop.Token)) {
                    try { output(frame); }
                    finally { free.Enqueue(frame); }
                }
            } catch { stop.Cancel(); throw; }
        }, CancellationToken.None);

        Exception? readError = null;
        try {
            while (!stop.IsCancellationRequested) {
                if (!free.TryDequeue(out var frame))
                    throw new InvalidOperationException("Decoded frame buffer ownership lost.");
                try {
                    // A partial raw frame cannot be presented or skipped bytewise.
                    var first = await input.ReadAsync(frame.AsMemory(0, frameBytes), stop.Token);
                    if (first == 0) break;
                    await input.ReadExactlyAsync(frame.AsMemory(first, frameBytes - first), stop.Token);
                    if (!latest.Writer.TryWrite(frame)) break;
                    frame = null;
                } finally { if (frame != null) free.Enqueue(frame); }
            }
        } catch (Exception ex) { readError = ex; }
        finally { latest.Writer.TryComplete(); }
        // Observe consumer failures before a cancellation caused by that failure.
        await consumer;
        if (readError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(readError).Throw();
        cancellation.ThrowIfCancellationRequested();
    }
}
