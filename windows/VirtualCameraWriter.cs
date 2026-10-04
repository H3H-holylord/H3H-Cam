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

    public static bool IsInstalled() {
        using var driver = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
            @"CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\InprocServer32");
        return driver != null;
    }

    public static string? FindDriverDll() {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[] {
            Path.Combine(baseDir, "tools", "virtualcam", "obs-virtualcam-module64.dll"),
            Path.Combine(baseDir, "obs-virtualcam-module64.dll"),
            Path.Combine(Environment.CurrentDirectory, "tools", "virtualcam", "obs-virtualcam-module64.dll"),
            Path.Combine(Environment.CurrentDirectory, "windows", "tools", "virtualcam", "obs-virtualcam-module64.dll"),
            @"C:\Program Files\obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static readonly byte[] s_dshowFilterData = [
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x30, 0x70, 0x69, 0x33, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x30, 0x74, 0x79, 0x33, 0x00, 0x00, 0x00, 0x00,
        0x38, 0x00, 0x00, 0x00, 0x48, 0x00, 0x00, 0x00, 0x76, 0x69, 0x64, 0x73, 0x00, 0x00, 0x10, 0x00,
        0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71, 0x4E, 0x56, 0x31, 0x32, 0x00, 0x00, 0x10, 0x00,
        0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
    ];

    public static bool InstallUserLevel() {
        var dll = FindDriverDll();
        if (dll == null) return false;
        try {
            const string clsid = "{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
            const string catId = "{860BB310-5D01-11d0-BD3B-00A0C911CE86}";

            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}")) {
                key.SetValue(null, "H3H Cam (Virtual Camera)");
                using var inproc = key.CreateSubKey("InprocServer32");
                inproc.SetValue(null, Path.GetFullPath(dll));
                inproc.SetValue("ThreadingModel", "Both");
            }

            using (var catKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{catId}\Instance\{clsid}")) {
                catKey.SetValue("CLSID", clsid);
                catKey.SetValue("FriendlyName", "H3H Cam");
                catKey.SetValue("FilterData", s_dshowFilterData, Microsoft.Win32.RegistryValueKind.Binary);
            }

            return IsInstalled();
        } catch {
            return false;
        }
    }

    public static bool UninstallUserLevel() {
        try {
            const string clsid = "{A3FCE0F5-3493-419F-958A-ABA1250EC20B}";
            const string catId = "{860BB310-5D01-11d0-BD3B-00A0C911CE86}";
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{clsid}", false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{catId}\Instance\{clsid}", false);
            return true;
        } catch {
            return false;
        }
    }

    public static async Task<bool> InstallDriverAsync(bool forceElevated = false) {
        if (!forceElevated && InstallUserLevel()) {
            return true;
        }

        var dll = FindDriverDll();
        if (dll == null) throw new FileNotFoundException("Файл obs-virtualcam-module64.dll не найден в tools/virtualcam/");
        var psi = new ProcessStartInfo {
            FileName = "regsvr32.exe",
            Arguments = $"/s \"{dll}\"",
            UseShellExecute = true,
            Verb = "runas"
        };
        try {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
            return IsInstalled();
        } catch { return false; }
    }

    public static async Task<bool> UninstallDriverAsync() {
        UninstallUserLevel();
        var dll = FindDriverDll();
        if (dll == null) {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}\InprocServer32");
            dll = k?.GetValue(null) as string;
        }
        if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll)) return !IsInstalled();
        var psi = new ProcessStartInfo {
            FileName = "regsvr32.exe",
            Arguments = $"/u /s \"{dll}\"",
            UseShellExecute = true,
            Verb = "runas"
        };
        try {
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
            return !IsInstalled();
        } catch { return !IsInstalled(); }
    }

    public VirtualCameraWriter(int width, int height, int fps) {
        if (!IsInstalled()) throw new IOException("Не установлен драйвер DirectShow Virtual Camera. Нажмите «Установить в 1 клик» в окне настроек H3H Cam.");
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
