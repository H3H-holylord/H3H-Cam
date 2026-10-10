using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using S8Cam;

internal static class VirtualCameraDriverTests {
    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception("Virtual camera: " + message);
    }

    public static void Run(string root) {
        var fixture = Path.Combine(root, "virtualcam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var source64 = VirtualCameraDriver.FindDriverDll();
        var source32 = VirtualCameraDriver.FindDriverDll(x64: false);
        Check(source64 != null && source32 != null, "both driver architectures must be packaged");
        Check(!VirtualCameraDriver.IsDriverBinary(source32!, x64: true), "reject wrong-architecture DLL");
        var badDll = Path.Combine(fixture, "broken.dll");
        File.WriteAllText(badDll, "not a PE module");
        Check(!VirtualCameraDriver.IsDriverBinary(badDll, x64: true), "reject corrupt DLL");

        foreach (bool x64 in new[] { true, false }) {
            string keyPath = @"Software\H3HCamDriverTests\" + Guid.NewGuid().ToString("N");
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,
                x64 ? RegistryView.Registry64 : RegistryView.Registry32);
            try {
                using var classes = user.CreateSubKey(keyPath);
                Check(!VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "clean registry is not installed");
                using (var server = classes.CreateSubKey($@"CLSID\{VirtualCameraDriver.CameraClsid}\InprocServer32"))
                    server.SetValue(null, x64 ? source64! : source32!);
                Check(!VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "a COM key alone is not a camera");

                var unpacked = Path.Combine(fixture, x64 ? "zip64" : "zip32");
                Directory.CreateDirectory(unpacked);
                var source = Path.Combine(unpacked, x64 ? "obs-virtualcam-module64.dll" : "obs-virtualcam-module32.dll");
                File.Copy(x64 ? source64! : source32!, source);
                var stableDir = Path.Combine(fixture, "appdata");
                var installed = VirtualCameraDriver.InstallInto(classes, source, stableDir, x64);
                Check(VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "registered camera is ready");
                Check(installed.StartsWith(stableDir + Path.DirectorySeparatorChar), "server uses persistent storage");
                File.Move(source, source + ".moved");
                Check(VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "moving portable files keeps the camera registered");
                File.Move(installed, installed + ".missing");
                Check(!VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "dangling server path must not show ready");
                File.Move(installed + ".missing", installed);
                classes.DeleteSubKeyTree($@"CLSID\{VirtualCameraDriver.CameraCategory}\Instance\{VirtualCameraDriver.CameraClsid}");
                Check(!VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "missing device-category entry is detected");
                var repaired = VirtualCameraDriver.InstallInto(classes, source + ".moved", stableDir, x64);
                Check(VirtualCameraDriver.ReadRegistration(classes, x64).Ready, "repair restores camera enumeration metadata");
                Check(File.Exists(repaired), "repair stores the DLL");
            } finally {
                // Only this test's random private registry subtree is changed or removed.
                user.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
        }
        ProbeNativeFilter(source64!);
        Console.WriteLine("PASS virtual camera: packaged x64/x86, isolated registration, stale paths, persistent DLL and native IBaseFilter");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetClassObject(ref Guid clsid, ref Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInstance(nint factory, nint outer, ref Guid iid, out nint filter);

    private static void ProbeNativeFilter(string dll) {
        nint module = NativeLibrary.Load(dll);
        nint factory = 0, filter = 0;
        try {
            var getClass = Marshal.GetDelegateForFunctionPointer<GetClassObject>(NativeLibrary.GetExport(module, "DllGetClassObject"));
            var clsid = new Guid(VirtualCameraDriver.CameraClsid);
            var factoryIid = new Guid("00000001-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(getClass(ref clsid, ref factoryIid, out factory));
            var vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateInstance>(Marshal.ReadIntPtr(vtable, IntPtr.Size * 3));
            var filterIid = new Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770");
            Marshal.ThrowExceptionForHR(create(factory, 0, ref filterIid, out filter));
            Check(filter != 0, "packaged DLL activates the actual DirectShow capture filter");
        } finally {
            if (filter != 0) Marshal.Release(filter);
            if (factory != 0) Marshal.Release(factory);
            NativeLibrary.Free(module);
        }
    }
}
