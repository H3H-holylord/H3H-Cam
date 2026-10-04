using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static unsafe class StressLoad {
    private static double sink;
    public static void Cpu(int seconds, double duty) {
        using var process=Process.GetCurrentProcess(); process.PriorityClass=ProcessPriorityClass.Normal;
        var clock=Stopwatch.StartNew();
        var workers=Enumerable.Range(0,Environment.ProcessorCount).Select(i => new Thread(() => {
            double value=i+1;
            Thread.Sleep(i*100/Environment.ProcessorCount);
            while (clock.Elapsed.TotalSeconds<seconds) {
                double until=clock.Elapsed.TotalMilliseconds+100*duty;
                while (clock.Elapsed.TotalMilliseconds<until) {
                    for (int j=0;j<256;j++) value=Math.Sqrt(value+1.00001);
                    Volatile.Write(ref sink,value);
                }
                Thread.Sleep(Math.Max(1,(int)(100*(1-duty))));
            }
        }) {IsBackground=true}).ToArray();
        foreach(var thread in workers)thread.Start();
        foreach(var thread in workers)thread.Join();
        Console.WriteLine($"CPU load finished: {workers.Length} workers, duty {duty:P0}");
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc {
        public uint Width,Height,Mips,ArraySize,Format,SampleCount,SampleQuality,Usage,BindFlags,CpuAccess,Misc;
    }
    [StructLayout(LayoutKind.Sequential)] private struct QueryDesc {public uint Kind,Flags;}
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,int type,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out int level,out nint context);
    [DllImport("d3dcompiler_47.dll")] private static extern int D3DCompile(byte[] source,nuint length,string? name,nint defines,nint include,string entry,string target,uint flags,uint flags2,out nint code,out nint errors);
    public static void Gpu(int seconds,double duty) {
        using var process=Process.GetCurrentProcess();process.PriorityClass=ProcessPriorityClass.AboveNormal;
        nint device=0,context=0,texture=0,uav=0,shader=0,query=0,blob=0,errors=0;
        void Check(int hr) {if(hr<0)Marshal.ThrowExceptionForHR(hr);}
        try {
            Check(D3D11CreateDevice(0,1,0,0,0,0,7,out device,out _,out context));
            var dev=*(nint**)device;var ctx=*(nint**)context;
            var desc=new TextureDesc {Width=1024,Height=1024,Mips=1,ArraySize=1,Format=41,SampleCount=1,BindFlags=128};
            Check(((delegate* unmanaged[Stdcall]<nint,in TextureDesc,nint,out nint,int>)dev[5])(device,in desc,0,out texture));
            Check(((delegate* unmanaged[Stdcall]<nint,nint,nint,out nint,int>)dev[8])(device,texture,0,out uav));
            var source=Encoding.ASCII.GetBytes("RWTexture2D<float> target:register(u0); [numthreads(16,16,1)] void main(uint3 id:SV_DispatchThreadID){float x=(id.x+id.y*0.013)*0.0001; [loop] for(uint i=0;i<512;i++){x=sin(x*1.013+0.017)+cos(x*0.977+0.031);} target[id.xy]=x;}");
            Check(D3DCompile(source,(nuint)source.Length,null,0,0,"main","cs_5_0",0,0,out blob,out errors));
            var bv=*(nint**)blob;
            var pointer=((delegate* unmanaged[Stdcall]<nint,nint>)bv[3])(blob);
            var size=((delegate* unmanaged[Stdcall]<nint,nuint>)bv[4])(blob);
            Check(((delegate* unmanaged[Stdcall]<nint,nint,nuint,nint,out nint,int>)dev[18])(device,pointer,size,0,out shader));
            var q=new QueryDesc(); Check(((delegate* unmanaged[Stdcall]<nint,in QueryDesc,out nint,int>)dev[24])(device,in q,out query));
            ((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,uint*,void>)ctx[68])(context,0,1,&uav,null);
            ((delegate* unmanaged[Stdcall]<nint,nint,nint*,uint,void>)ctx[69])(context,shader,null,0);
            var clock=Stopwatch.StartNew();int batches=0;double maximumBatch=0;
            while(clock.Elapsed.TotalSeconds<seconds) {
                var start=clock.Elapsed.TotalMilliseconds;
                // One bounded batch in flight; do not accumulate a driver queue.
                for(int batch=0;batch<8;batch++)
                    ((delegate* unmanaged[Stdcall]<nint,uint,uint,uint,void>)ctx[41])(context,64,64,1);
                ((delegate* unmanaged[Stdcall]<nint,nint,void>)ctx[28])(context,query);
                ((delegate* unmanaged[Stdcall]<nint,void>)ctx[111])(context);
                int hr,complete;
                do {
                    complete=0;
                    hr=((delegate* unmanaged[Stdcall]<nint,nint,nint,uint,uint,int>)ctx[29])(context,query,(nint)(&complete),4,0);
                    Check(hr);
                    if(hr!=1&&complete!=0)break;
                    if(clock.Elapsed.TotalMilliseconds-start>1500)throw new TimeoutException("GPU load batch exceeded 1.5s; stop load");
                    Thread.SpinWait(256);
                } while(true);
                Check(hr);
                var busy=clock.Elapsed.TotalMilliseconds-start;maximumBatch=Math.Max(maximumBatch,busy);batches++;
                if(duty<1) {
                    double restUntil=clock.Elapsed.TotalMilliseconds+busy*(1-duty)/duty;
                    while(clock.Elapsed.TotalMilliseconds<restUntil)Thread.SpinWait(128);
                }
            }
            Console.WriteLine($"GPU load finished: {batches} bounded batches, max {maximumBatch:F1} ms, duty {duty:P0}");
        } finally {
            foreach(var resource in new[]{errors,blob,query,shader,uav,texture,context,device})if(resource!=0)Marshal.Release(resource);
        }
    }
}
