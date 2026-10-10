namespace S8Cam;

/// <summary>Optional preview has one replaceable pending frame and never runs on the capture worker.</summary>
public sealed class LatestFramePreview : IAsyncDisposable {
    private readonly object gate=new();
    private readonly AutoResetEvent ready=new(false);
    private readonly TaskCompletionSource finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PreviewSampler sampler=new();
    private readonly Action<byte[],int,int> output;
    private readonly Action<string> log;
    private readonly int width,height,previewWidth,previewHeight,bytes,nv12Bytes;
    private byte[]? first,second,pending,processing;
    private bool disposed;
    private volatile bool enabled;
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public LatestFramePreview(int width,int height,int previewWidth,int previewHeight,Action<byte[],int,int> output,Action<string> log) {
        this.width=width;this.height=height;this.previewWidth=previewWidth;this.previewHeight=previewHeight;
        bytes=checked(width*height*4);nv12Bytes=checked(width*height*3/2);this.output=output;this.log=log;
        new Thread(Work) {IsBackground=true,Name="H3H preview",Priority=ThreadPriority.BelowNormal}.Start();
    }
    public void SetEnabled(bool value) {
        enabled=value;
        if(!value)lock(gate) {pending=null;}
    }
    public void Submit(byte[] source) {
        if(!enabled)return;
        int len=source.Length;
        if(len!=bytes && len!=nv12Bytes)throw new ArgumentException("Preview source size changed");
        lock(gate) {
            if(disposed||!enabled)return;
            if(first==null||first.Length!=len) first=new byte[len];
            if(second==null||second.Length!=len) second=new byte[len];
            var target=ReferenceEquals(processing,first)?second:first;
            if(pending!=null)Interlocked.Increment(ref dropped);
            Buffer.BlockCopy(source,0,target,0,len);
            pending=target;ready.Set();
        }
    }
    private void Work() {
        try {
            while(true) {
                ready.WaitOne();
                byte[]? source;
                lock(gate) {if(disposed)return;source=pending;pending=null;processing=source;}
                if(source==null)continue;
                try {
                    if(enabled) {
                        var preview=source.Length==nv12Bytes
                            ? sampler.SampleNv12(source,width,height,previewWidth,previewHeight)
                            : sampler.Sample(source,width,height,previewWidth,previewHeight);
                        if(enabled)output(preview,previewWidth,previewHeight);
                    }
                } catch(Exception ex) {
                    // Optional observers must not terminate a background thread/process.
                    try {log(L.Get("s_5001b2a00821")+ex.Message);} catch { }
                }
                finally {lock(gate) {processing=null;}}
            }
        } finally {ready.Dispose();finished.TrySetResult();}
    }
    public async ValueTask DisposeAsync() {
        lock(gate) {if(!disposed) {disposed=true;enabled=false;pending=null;ready.Set();}}
        await finished.Task;
    }
}
