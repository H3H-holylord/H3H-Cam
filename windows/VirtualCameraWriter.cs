using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace S8Cam;

/// <summary>NV12 producer for the installed OBS Virtual Camera driver's shared-memory ABI.
/// Protocol: OBS Studio shared/obs-shared-memory-queue, documented in THIRD_PARTY.md.
/// Three slots hold the latest frame; consumers never need to drain an old frame queue.</summary>
public sealed class VirtualCameraWriter : IDisposable {
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly int frameBytes;
    private readonly int stride;
    private uint sequence;
    private bool disposed;
    public long Frames { get; private set; }

    public static bool IsInstalled() => VirtualCameraDriver.GetStatus().Ready;
    public static string? FindDriverDll() => VirtualCameraDriver.FindDriverDll();
    public static bool InstallUserLevel() => VirtualCameraDriver.EnsureInstalled();
    public static bool UninstallUserLevel() => VirtualCameraDriver.UninstallUserLevel();
    public static Task<bool> InstallDriverAsync() => VirtualCameraDriver.InstallAsync();

    public VirtualCameraWriter(int width, int height, int fps) {
        if (!VirtualCameraDriver.EnsureInstalled()) throw new IOException("Не удалось зарегистрировать виртуальную камеру");
        if (width % 2 != 0 || height % 2 != 0 || width < 2 || height < 2 || fps < 1)
            throw new ArgumentException("NV12 needs even dimensions and positive FPS");
        frameBytes = checked(width * height * 3 / 2);
        stride = (frameBytes + 32 + 31) & ~31;
        try {
            mapping = MemoryMappedFile.CreateOrOpen("OBSVirtualCamVideo", 96L + 3L * stride,
                MemoryMappedFileAccess.ReadWrite);
        } catch (Exception ex) {
            throw new IOException("Не удалось открыть память OBS Virtual Camera: " + ex.Message, ex);
        }
        try {
            view = mapping.CreateViewAccessor();
            view.Write(8, 1u); // STARTING
            for (var i = 0; i < 3; i++) view.Write(12 + i * 4, (uint)(96 + i * stride));
            view.Write(24, 0u); // video
            view.Write(28, (uint)width);
            view.Write(32, (uint)height);
            view.Write(40, (ulong)(10_000_000 / fps)); // 100 ns interval
        } catch { mapping.Dispose(); throw; }
    }

    public unsafe void Write(byte[] nv12) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (nv12.Length != frameBytes) throw new ArgumentException("NV12 frame size mismatch");
        var next = unchecked(++sequence);
        var offset = 96L + next % 3 * stride;
        view.Write(0, next);
        view.Write(offset, (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency)));
        byte* destination = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref destination);
        try {
            fixed (byte* source = nv12)
                Buffer.MemoryCopy(source, destination + view.PointerOffset + offset + 32, frameBytes, frameBytes);
        } finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        Thread.MemoryBarrier();
        view.Write(4, next);
        view.Write(8, 2u); // READY
        Frames++;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        view.Write(8, 3u); // STOPPING
        Thread.MemoryBarrier();
        view.Dispose();
        mapping.Dispose();
    }
}
