using System;
using System.Runtime.InteropServices;

namespace S8Cam;

/// <summary>
/// Native Windows 10 / Windows 11 Media Foundation Virtual Camera subsystem.
/// Bridges camera feeds directly into modern Windows applications (MS Teams, Edge, Chrome, UWP).
/// </summary>
public sealed class MediaFoundationVirtualCamera : IDisposable {
    public enum MFVirtualCameraType {
        SoftwareCameraSource = 0
    }

    public enum MFVirtualCameraLifetime {
        Session = 0,
        System = 1
    }

    public enum MFVirtualCameraAccess {
        CurrentUser = 0,
        AllUsers = 1
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libname);

    [DllImport("mfsensorgroup.dll", ExactSpelling = true)]
    public static extern int MFCreateVirtualCamera(
        MFVirtualCameraType type,
        MFVirtualCameraLifetime lifetime,
        MFVirtualCameraAccess access,
        [MarshalAs(UnmanagedType.LPWStr)] string friendlyName,
        [MarshalAs(UnmanagedType.LPWStr)] string vcamId,
        [MarshalAs(UnmanagedType.LPArray)] Guid[] categories,
        uint categoryCount,
        out IntPtr ppVirtualCamera
    );

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    private static readonly Lazy<bool> s_isSupported = new(() => {
        try {
            var hModule = LoadLibrary("mfsensorgroup.dll");
            if (hModule == IntPtr.Zero) return false;
            var proc = GetProcAddress(hModule, "MFCreateVirtualCamera");
            return proc != IntPtr.Zero;
        } catch {
            return false;
        }
    });

    public static bool IsSupported => s_isSupported.Value;

    private IntPtr virtualCamPtr = IntPtr.Zero;
    private bool mfStarted = false;
    private readonly object lockObj = new();

    public bool IsActive => virtualCamPtr != IntPtr.Zero;

    public bool Start(string friendlyName = "H3H Cam (Media Foundation)") {
        if (!IsSupported) return false;

        lock (lockObj) {
            if (virtualCamPtr != IntPtr.Zero) return true;

            try {
                int hr = MFStartup(0x00020070, 0); // MF_VERSION
                if (hr != 0) return false;
                mfStarted = true;

                var kscategoryVideoCamera = new Guid("E5323777-F976-4f5b-9B55-B94699C46E44");
                var categories = new[] { kscategoryVideoCamera };

                int res = MFCreateVirtualCamera(
                    MFVirtualCameraType.SoftwareCameraSource,
                    MFVirtualCameraLifetime.Session,
                    MFVirtualCameraAccess.CurrentUser,
                    friendlyName,
                    "{A8C2B3E1-4209-4D89-A45F-9876543210AB}",
                    categories,
                    1,
                    out virtualCamPtr
                );

                return res == 0 && virtualCamPtr != IntPtr.Zero;
            } catch {
                Stop();
                return false;
            }
        }
    }

    public void Stop() {
        lock (lockObj) {
            if (virtualCamPtr != IntPtr.Zero) {
                try {
                    Marshal.Release(virtualCamPtr);
                } catch { }
                virtualCamPtr = IntPtr.Zero;
            }

            if (mfStarted) {
                try {
                    MFShutdown();
                } catch { }
                mfStarted = false;
            }
        }
    }

    public void Dispose() {
        Stop();
    }
}
