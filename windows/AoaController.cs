using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace S8Cam;

public sealed record AoaProbeResult(bool Ready, bool AccessoryMode, string Status, ushort VendorId = 0, ushort ProductId = 0);

/// <summary>Android Open Accessory negotiation. Does not require Android USB debugging.</summary>
public sealed class AoaController : IDisposable {
    private const ushort GoogleVid = 0x18D1;
    private static readonly HashSet<ushort> AccessoryPids = [0x2D00, 0x2D01, 0x2D04, 0x2D05];
    private UsbContext? context;
    private IUsbDevice? device;
    private UsbEndpointReader? reader;
    private UsbEndpointWriter? writer;
    private UsbReaderStream? readerStream;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public async Task<AoaProbeResult> ConnectAsync(CancellationToken ct, bool allowSwitch = true) {
        ct.ThrowIfCancellationRequested();
        if (!NativeLibrary.TryLoad("libusb-1.0.dll", out var native))
            return new(false, false, L.Get("s_1f1a0011b99a"));
        NativeLibrary.Free(native);
        try {
            if (context == null) {
                context = new UsbContext();
                GC.SuppressFinalize(context);
            }
        }
        catch (Exception ex) {
            return new(false, false, L.Get("s_0c61bc930a2b") + ex.Message);
        }
        // Find returns an owned clone and disposes the enumeration while its native
        // context is alive. Letting enumerated devices reach GC after libusb_exit
        // causes an uncatchable AccessViolation in libusb_unref_device.
        var current = context.Find(IsAccessory);
        if (current == null) {
            if (!allowSwitch) return new(false, false, L.Get("s_02c6e34daac7"));
            var switched = false;
            using (var candidates=context.List()) {
                foreach (var candidate in candidates) {
                    ct.ThrowIfCancellationRequested();
                    try { if (TryStartAccessory(candidate)) { switched = true; break; } }
                    catch { candidate.Close(); }
                }
            }
            if (!switched)
                return new(false, false, L.Get("s_b4d1d6d4472d"));
            for (var i = 0; i < 30 && current == null; i++) {
                await Task.Delay(200, ct);
                current = context.Find(IsAccessory);
            }
        }
        if (current == null) return new(false, false, L.Get("s_bbfd73bcd331"));
        bool connected=false;
        try {
            current.Open();
            current.ClaimInterface(0);
            reader = current.OpenEndpointReader(ReadEndpointID.Ep01, 64 * 1024);
            writer = current.OpenEndpointWriter(WriteEndpointID.Ep01);
            readerStream = new UsbReaderStream(reader);
            device = current;
            connected=true;
            return new(true, true, L.Get("s_96719d5d11f9"), current.VendorId, current.ProductId);
        } catch (Exception ex) {
            reader=null;
            writer=null;
            readerStream?.Dispose();readerStream=null;
            current.Close();
            return new(false, true, L.Get("s_068401dded37") + ex.Message,
                current.VendorId, current.ProductId);
        } finally {if(!connected)current.Dispose();}
    }

    private readonly byte[] headerBuffer = new byte[H3HProtocol.HeaderSize];

    public async ValueTask<H3HMessage?> ReadAsync(CancellationToken ct) {
        if (readerStream == null) throw new InvalidOperationException("USB Direct is not connected");
        return await H3HProtocol.ReadAsync(readerStream, headerBuffer, ct);
    }

    public async ValueTask WriteAsync(H3HMessage message, CancellationToken ct) {
        if (writer == null) throw new InvalidOperationException("USB Direct is not connected");
        await writeLock.WaitAsync(ct);
        try {
            var packet = H3HProtocol.Encode(message);
            var offset = 0;
            while (offset < packet.Length) {
                var length = Math.Min(16 * 1024, packet.Length - offset);
                var (error, transferred) = await writer.WriteAsync(packet.AsMemory(offset, length), 1000);
                if (error != Error.Success || transferred <= 0) throw new IOException($"USB write: {error}");
                offset += transferred;
                ct.ThrowIfCancellationRequested();
            }
        } finally {
            writeLock.Release();
        }
    }

    private static bool IsAccessory(IUsbDevice d) => d.VendorId == GoogleVid && AccessoryPids.Contains(d.ProductId);

    private static bool TryStartAccessory(IUsbDevice d) {
        if (!d.TryOpen()) return false;
        var protocol = new byte[2];
        var read = d.ControlTransfer(new UsbSetupPacket(0xC0, 51, 0, 0, 2), protocol, 0, 2);
        if (read != 2 || BitConverter.ToUInt16(protocol) < 1) { d.Close(); return false; }
        var strings = new[] { "H3H", "H3H Cam", "Low latency Android camera", "4.0", "https://github.com/", "H3HCAM" };
        for (var i = 0; i < strings.Length; i++) {
            var bytes = Encoding.UTF8.GetBytes(strings[i] + "\0");
            var wrote = d.ControlTransfer(new UsbSetupPacket(0x40, 52, 0, i, bytes.Length), bytes, 0, bytes.Length);
            if (wrote != bytes.Length) throw new IOException("AOA identity transfer failed");
        }
        d.ControlTransfer(new UsbSetupPacket(0x40, 53, 0, 0, 0));
        d.Close();
        return true;
    }

    public void Dispose() {
        readerStream?.Dispose();
        readerStream = null;
        reader = null;
        writer = null;
        try { device?.ReleaseInterface(0); } catch { }
        try { device?.Close(); } catch { }
        try { device?.Dispose(); } catch { }
        device = null;
        if (context != null) {
            try { GC.SuppressFinalize(context); } catch { }
            try { context.Dispose(); } catch { }
            context = null;
        }
        try { writeLock.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class UsbReaderStream(UsbEndpointReader source) : Stream {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            while (!ct.IsCancellationRequested) {
                var (error, transferred) = await source.ReadAsync(buffer, 1000);
                ct.ThrowIfCancellationRequested();
                if (error is Error.Success or Error.Timeout && transferred > 0) return transferred;
                if (error == Error.Timeout) {
                    continue; // Timeout waiting for data on bulk endpoint - retry read loop
                }
                if (error != Error.Success) throw new IOException($"USB read: {error}");
                return transferred;
            }
            throw new OperationCanceledException(ct);
        }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
