using S8Cam;

internal static class VideoSchedulingTests {
    public static async Task Preview() {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var release=new ManualResetEventSlim();
        var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newest=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen=new List<byte>();
        await using var preview=new LatestFramePreview(16,16,16,16,(frame,_,_)=>{
            byte id=frame[0];
            if(id==0) {first.TrySetResult();release.Wait(timeout.Token);if(frame[0]!=0)throw new Exception("Preview frame overwritten");}
            seen.Add(id);if(id==200)newest.TrySetResult();
        },message=>throw new Exception(message));
        preview.SetEnabled(true);
        var raw=new byte[16*16*4];preview.Submit(raw);
        try {
            await first.Task.WaitAsync(timeout.Token);
            for(int i=1;i<=200;i++) {Array.Fill(raw,(byte)i);preview.Submit(raw);}
        } finally {release.Set();}
        await newest.Task.WaitAsync(timeout.Token);
        preview.SetEnabled(false);Array.Fill(raw,(byte)99);preview.Submit(raw);
        await preview.DisposeAsync();
        if(!seen.SequenceEqual(new byte[]{0,200})||preview.Dropped<100)throw new Exception("Preview keeps a backlog instead of latest frame");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int errors=0;
        await using(var failing=new LatestFramePreview(16,16,8,8,(_,_,_)=>throw new Exception("optional preview failure"),_=>{Interlocked.Increment(ref errors);ready.TrySetResult();})) {
            failing.SetEnabled(true);failing.Submit(raw);await ready.Task.WaitAsync(timeout.Token);
        }
        if(errors!=1)throw new Exception("Preview failure not isolated");
        var result=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>{using var scheduling=new VideoThreadScheduling();result.TrySetResult(scheduling.Registered);}) {IsBackground=true}.Start();
        Console.WriteLine("MMCSS Capture available: "+await result.Task.WaitAsync(timeout.Token));
        Console.WriteLine("PASS preview worker · latest frame · buffer ownership · hidden · failure isolation · cleanup");
    }
}
