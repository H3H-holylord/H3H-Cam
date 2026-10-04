using System.Runtime.InteropServices;

namespace S8Cam;

/// <summary>MMCSS applies only to this dedicated capture worker and is reverted on it.</summary>
public sealed class VideoThreadScheduling : IDisposable {
    [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern nint AvSetMmThreadCharacteristics(string task,ref uint index);
    [DllImport("avrt.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AvSetMmThreadPriority(nint handle,int priority);
    [DllImport("avrt.dll")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(nint handle);
    private nint handle;
    private readonly ThreadPriority originalPriority;
    public bool Registered => handle!=0;
    public VideoThreadScheduling() {
        originalPriority=Thread.CurrentThread.Priority;
        try {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            uint index=0; handle=AvSetMmThreadCharacteristics("Capture",ref index);
            if(handle!=0)AvSetMmThreadPriority(handle,1); // AVRT_PRIORITY_HIGH
        } catch (Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception or ThreadStateException) { }
    }
    public void Dispose() {
        if(handle!=0) {AvRevertMmThreadCharacteristics(handle);handle=0;}
        Thread.CurrentThread.Priority=originalPriority;
    }
}
