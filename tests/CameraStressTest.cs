using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using S8Cam;

internal static class CameraStressTest {
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(nint process);
    private sealed record PhaseReport(string Phase, int RequestedFps, double Seconds, double OutputFps,
        double SpoutFps, long GpuSkipped, double OutputP99Ms, double OutputMaxGapMs,
        double PreviewFps, double PreviewP99Ms, double PreviewMaxGapMs, double CpuAverage, double? GpuAverage);
    public static async Task Run(string root,int fps,int slowPreviewMs) {
        Directory.CreateDirectory(root);
        using(var process=Process.GetCurrentProcess())Processes.PrioritizeVideo(process);
        var backup=File.ReadAllText(Settings.FilePath);
        var settings=Settings.Load().Clone();
        settings.Transport="usb";settings.Width=1920;settings.Height=1080;settings.Fps=fps;
        settings.Codec="hevc";settings.BitrateMbps=fps==60?32:20;
        settings.Preview=true;settings.SpoutOutput=true;settings.VirtualCamera=true;settings.Obs=false;
        settings.AdaptiveBitrate=false;settings.WifiLimitMbps=0;
        var timer=Stopwatch.StartNew();var sync=new object();var previews=new List<double>();var outputs=new List<double>();int errors=0;
        await using var engine=new ReceiverEngine(settings,line=>{Console.WriteLine(line);if(line.Contains("Error constructing")||line.Contains("Could not find ref"))Interlocked.Increment(ref errors);});
        engine.PreviewFrame+=(_,_,_)=>{lock(sync)previews.Add(timer.Elapsed.TotalMilliseconds);if(slowPreviewMs>0)Thread.Sleep(slowPreviewMs);};
        engine.OutputFramePublished+=()=>{lock(sync)outputs.Add(timer.Elapsed.TotalMilliseconds);};
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var reports=new List<PhaseReport>();Process? cpu=null,gpu=null;
        async Task<PhaseReport> Phase(string name,int seconds) {
            long frames=engine.VirtualCameraFrames;long spoutFrames=engine.SpoutFrames;long gpuSkipped=engine.GpuDroppedFrames;double start=timer.Elapsed.TotalMilliseconds;
            var samples=new List<double>();var gpuSamples=new List<double>();
            GetSystemTimes(out var idle,out var kernel,out var user);
            while(timer.Elapsed.TotalMilliseconds-start<seconds*1000) {
                await Task.Delay(1000,limit.Token);
                GetSystemTimes(out var nextIdle,out var nextKernel,out var nextUser);
                double total=(nextKernel-kernel)+(nextUser-user);
                samples.Add(total>0?(1-(nextIdle-idle)/total)*100:0);
                idle=nextIdle;kernel=nextKernel;user=nextUser;
                try {
                    var query=await Processes.Run(Path.Combine(Environment.SystemDirectory,"nvidia-smi.exe"),["--query-gpu=utilization.gpu","--format=csv,noheader,nounits"],limit.Token,4000);
                    if(double.TryParse(query.Split('\n')[0],out var load))gpuSamples.Add(load);
                } catch { }
            }
            double end=timer.Elapsed.TotalMilliseconds;
            double[] gaps, outputGaps;
            double[] Gaps(List<double> times) {
                var p=times.Where(t=>t>=start&&t<=end).ToArray();
                return p.Zip(p.Skip(1),(a,b)=>b-a).Order().ToArray();
            }
            double P99(double[] values)=>values.Length>0?values[(int)((values.Length-1)*.99)]:0;
            lock(sync) {gaps=Gaps(previews);outputGaps=Gaps(outputs);}
            var result=new PhaseReport(name,fps,(end-start)/1000,
                (engine.VirtualCameraFrames-frames)*1000/(end-start),
                (engine.SpoutFrames-spoutFrames)*1000/(end-start),engine.GpuDroppedFrames-gpuSkipped,
                P99(outputGaps),outputGaps.LastOrDefault(),gaps.Length>0?1000/gaps.Average():0,
                P99(gaps),gaps.LastOrDefault(),samples.Average(),gpuSamples.Count>0?gpuSamples.Average():null);
            Console.WriteLine("PHASE "+JsonSerializer.Serialize(result));return result;
        }
        try {
            await engine.Start(limit.Token);await Task.Delay(5000,limit.Token);
            reports.Add(await Phase("idle",6));
            var exe=Path.Combine(AppContext.BaseDirectory,"S8Cam.Tests.exe");
            cpu=Processes.Start(exe,["cpu-load","20","0.5"],line=>Console.WriteLine("CPU LOAD "+line));
            gpu=Processes.Start(exe,["gpu-load","20","0.9"],line=>Console.WriteLine("GPU LOAD "+line));
            await Task.Delay(2000,limit.Token);
            reports.Add(await Phase("cpu-gpu-load",14));
            await cpu.WaitForExitAsync(limit.Token);await gpu.WaitForExitAsync(limit.Token);
            if(cpu.ExitCode!=0||gpu.ExitCode!=0)throw new Exception("Stress helper failed");
            reports.Add(await Phase("recovery",6));
            await engine.Stop();
            await File.WriteAllTextAsync(Path.Combine(root,"stress.json"),JsonSerializer.Serialize(new {Fps=fps,SlowPreviewMs=slowPreviewMs,DecodeErrors=errors,Phases=reports},new JsonSerializerOptions{WriteIndented=true}));
            if(errors>0)throw new Exception("Decode errors during stress");
            if(reports.Any(p=>p.OutputFps<fps*.95||p.SpoutFps<fps*.95||p.OutputP99Ms>100||p.OutputMaxGapMs>250))
                throw new Exception("Output cadence failed: inspect stress.json (95% FPS / P99 100ms / max 250ms)");
            Console.WriteLine("PASS stress-test · output cadence · bounded CPU/GPU load · recovery · no decode errors");
        } finally {
            Processes.Kill(cpu);Processes.Kill(gpu);cpu?.Dispose();gpu?.Dispose();
            await engine.Stop();File.WriteAllText(Settings.FilePath,backup);
        }
    }

    public static async Task Watchdog() {
        var backup=File.ReadAllText(Settings.FilePath);
        var settings=Settings.Load().Clone();
        settings.Transport="usb";settings.Preview=false;settings.SpoutOutput=true;settings.VirtualCamera=true;settings.Obs=false;
        settings.Width=1920;settings.Height=1080;settings.Fps=30;settings.Codec="hevc";settings.BitrateMbps=20;
        bool recovered=false;
        await using var engine=new ReceiverEngine(settings,line=>{
            Console.WriteLine(line);
            if(line.Contains("не публикует кадры"))Volatile.Write(ref recovered,true);
        });
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(50));
        Process? decoder=null;
        bool suspended=false;
        async Task Until(Func<bool> condition) {
            while(!condition())await Task.Delay(100,limit.Token);
        }
        try {
            await engine.Start(limit.Token);
            await Until(()=>engine.VirtualCameraFrames>300&&engine.VirtualDecoderProcessId.HasValue);
            decoder=Process.GetProcessById(engine.VirtualDecoderProcessId!.Value);
            int pid=decoder.Id;
            // Suspend only the decoder owned by this test, never an unrelated process.
            if(NtSuspendProcess(decoder.Handle)<0)throw new Exception("Could not suspend test decoder");
            suspended=true;
            var clock=Stopwatch.StartNew();
            await Until(()=>Volatile.Read(ref recovered)&&engine.VirtualDecoderProcessId is int current&&current!=pid&&engine.VirtualCameraFrames>60);
            Console.WriteLine($"PASS watchdog · suspended decoder replaced · live frames restored in {clock.Elapsed.TotalSeconds:F2}s");
        } finally {
            if(suspended&&decoder!=null) {
                try {if(!decoder.HasExited)NtResumeProcess(decoder.Handle);} catch(InvalidOperationException) { }
            }
            decoder?.Dispose();
            await engine.Stop();File.WriteAllText(Settings.FilePath,backup);
        }
    }
}
