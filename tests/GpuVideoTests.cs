using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using S8Cam;

internal static class GpuVideoTests {
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc {
        public uint Width,Height,Mips,ArraySize,Format,SampleCount,SampleQuality,Usage,BindFlags,CpuAccess,Misc;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped {public nint Data;public uint RowPitch,DepthPitch;}
    private static nint Field(SpoutSender sender,string name)=>(nint)typeof(SpoutSender).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(sender)!;
    private static unsafe byte[] ReadTexture(SpoutSender sender) {
        nint device=Field(sender,"device"),context=Field(sender,"context"),texture=Field(sender,"texture"),staging=0;
        var dev=*(nint**)device;var ctx=*(nint**)context;
        var desc=new TextureDesc {Width=(uint)sender.Width,Height=(uint)sender.Height,Mips=1,ArraySize=1,Format=87,SampleCount=1,Usage=3,CpuAccess=0x20000};
        int hr=((delegate* unmanaged[Stdcall]<nint,in TextureDesc,nint,out nint,int>)dev[5])(device,in desc,0,out staging);
        if(hr<0)Marshal.ThrowExceptionForHR(hr);
        try {
            ((delegate* unmanaged[Stdcall]<nint,nint,nint,void>)ctx[47])(context,staging,texture);
            hr=((delegate* unmanaged[Stdcall]<nint,nint,uint,int,uint,out Mapped,int>)ctx[14])(context,staging,0,1,0,out var map);
            if(hr<0)Marshal.ThrowExceptionForHR(hr);
            try {
                var pixels=new byte[sender.Width*sender.Height*4];
                for(int y=0;y<sender.Height;y++)Marshal.Copy(map.Data+(nint)(y*map.RowPitch),pixels,y*sender.Width*4,sender.Width*4);
                return pixels;
            } finally {((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)ctx[15])(context,staging,0);}
        } finally {if(staging!=0)Marshal.Release(staging);}
    }
    public static async Task Run(string root) {
        Directory.CreateDirectory(root);
        var settings=Settings.Load().Clone();
        settings.FfmpegPath=ToolPaths.Find("ffmpeg.exe",settings.FfmpegPath);
        settings.Width=64;settings.Height=32;settings.Fps=30;settings.SuperResolution4K=false;
        settings.OrientationMode="16:9";settings.BackgroundEffect="none";settings.SkinSmoothing=false;
        settings.ColorProfile="none";settings.Brightness=0;settings.Contrast=1;settings.Saturation=1;
        settings.WbRedGain=settings.WbGreenGain=settings.WbBlueGain=1;
        settings.SpoutOutput=true;settings.VirtualCamera=false;
        // Reserve an RTP/RTCP pair outside Windows' ephemeral range. Otherwise the
        // source can accidentally acquire the receiver port when opening its socket.
        int port=FindPortPair();
        string run=Guid.NewGuid().ToString("N");
        string sdp=Path.Combine(Path.GetFullPath(root),$"fixture-{run}.sdp");
        using var source=Processes.Start(settings.FfmpegPath,["-hide_banner","-loglevel","error","-re","-f","lavfi","-i",
            "nullsrc=s=64x32:r=30,geq=lum='if(lt(X,W/2),if(lt(Y,H/2),40,160),if(lt(Y,H/2),90,220))':cb=128:cr=128",
            "-pix_fmt","yuv420p","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-g","1","-bf","0",
            "-f","rtp","-sdp_file",sdp,$"rtp://127.0.0.1:{port}?pkt_size=1200"],Console.WriteLine);
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try {
            while(!File.Exists(sdp))await Task.Delay(30,limit.Token);
            foreach(int rotation in new[]{0,90,180,270})foreach(bool mirror in new[]{false,true}) {
                settings.Rotation=rotation;settings.FlipHorizontal=mirror;
                await using var output=new VirtualCameraOutput(settings,sdp,limit.Token,Console.WriteLine);
                byte[]? preview=null;int pw=0,ph=0;
                output.PreviewFrame+=(pixels,w,h)=>{pw=w;ph=h;Volatile.Write(ref preview,pixels.ToArray());};
                var capture=CaptureOnVideoThread(output);
                output.SetPreviewEnabled(true);
                while(output.SpoutFrames<12||Volatile.Read(ref preview)==null) {
                    if(!output.Alive)throw new Exception("Fixture decoder exited before publishing frames");
                    await Task.Delay(30,limit.Token);
                }
                var sender=(SpoutSender)typeof(VirtualCameraOutput).GetField("spout",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(output)!;
                if(!sender.GpuNv12Ready)throw new Exception("GPU NV12 shaders unavailable on this test device");
                var rendered=await capture.WaitAsync(limit.Token);
                byte[] expected=mirror?new byte[]{90,40,220,160}:new byte[]{40,90,160,220};
                expected=rotation switch {90=>new[]{expected[2],expected[0],expected[3],expected[1]},180=>new[]{expected[3],expected[2],expected[1],expected[0]},270=>new[]{expected[1],expected[3],expected[0],expected[2]},_=>expected};
                void CheckCorners(byte[] pixels,int w,int h) {
                    for(int i=0;i<4;i++) {
                        int x=(i%2==0?1:3)*w/4,y=(i<2?1:3)*h/4,index=(y*w+x)*4;
                        int gray=Math.Clamp((298*(expected[i]-16)+128)>>8,0,255);
                        for(int c=0;c<3;c++)if(Math.Abs(pixels[index+c]-gray)>4)throw new Exception($"Orientation {rotation}/{mirror}: corner {i} got {pixels[index+c]}, expected {gray}");
                    }
                }
                CheckCorners(rendered,sender.Width,sender.Height);CheckCorners(preview!,pw,ph);
                Console.WriteLine($"PASS FFmpeg -> NV12 -> GPU/preview orientation {rotation} mirror={mirror}");
            }
            Processes.Kill(source);
            string redSdp=Path.Combine(Path.GetFullPath(root),$"red709-{run}.sdp");
            using var redSource=Processes.Start(settings.FfmpegPath,["-hide_banner","-loglevel","error","-re","-f","lavfi","-i",
                "color=c=red:s=64x32:r=30","-vf","scale=out_color_matrix=bt709:out_range=tv",
                "-pix_fmt","yuv420p","-c:v","libx264","-preset","ultrafast","-tune","zerolatency","-g","1","-bf","0",
                "-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709",
                "-f","rtp","-sdp_file",redSdp,$"rtp://127.0.0.1:{port}?pkt_size=1200"],Console.WriteLine);
            try {
                while(!File.Exists(redSdp))await Task.Delay(30,limit.Token);
                settings.Rotation=0;settings.FlipHorizontal=false;
                await using var redOutput=new VirtualCameraOutput(settings,redSdp,limit.Token,Console.WriteLine);
                var redCapture=CaptureOnVideoThread(redOutput);
                while(redOutput.SpoutFrames<12) {
                    if(!redOutput.Alive)throw new Exception("BT.709 fixture decoder exited before publishing frames");
                    await Task.Delay(30,limit.Token);
                }
                var redSender=(SpoutSender)typeof(VirtualCameraOutput).GetField("spout",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(redOutput)!;
                var red=await redCapture.WaitAsync(limit.Token);
                if(red[0]>8||red[1]>8||red[2]<246)throw new Exception($"BT.709 input color not preserved: BGR={red[0]},{red[1]},{red[2]}");
                Console.WriteLine("PASS BT.709 input -> explicit NV12 matrix -> GPU red");
            } finally {Processes.Kill(redSource);}
            using var colorSender=new SpoutSender("H3HCamColorTest",16,16);
            var raw=Enumerable.Repeat((byte)100,24).ToArray();
            for(int i=16;i<24;i+=2){raw[i]=149;raw[i+1]=108;}
            foreach(string profile in new[]{"none","clean","warm","cold","teal_orange","noir"}) {
                while(!colorSender.TryWriteNv12Frame(raw,4,4,0,colorProfile:profile,brightness:.07,contrast:1.14,saturation:1.35,wbR:1.15f,wbG:.9f,wbB:1.03f))
                    await Task.Delay(5,limit.Token);
                var rendered=ReadTexture(colorSender);
                var rgb=new byte[64];SpoutSender.Nv12ToBgra(raw,rgb,4,4);
                StudioEffectsProcessor.ApplyEffects(rgb,rgb,4,4,"none",colorProfile:profile,brightness:.07,contrast:1.14,saturation:1.35,wbR:1.15f,wbG:.9f,wbB:1.03f);
                for(int c=0;c<3;c++)if(Math.Abs(rendered[c]-rgb[c])>4)throw new Exception($"GPU color {profile}: channel {c} differs from RGB contract");
                Console.WriteLine("PASS GPU color/WB/sliders "+profile);
            }
        } finally {Processes.Kill(source);}
    }
    private static Task<byte[]> CaptureOnVideoThread(VirtualCameraOutput output) {
        var result=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.FramePublished+=()=>{
            if(output.SpoutFrames<12||result.Task.IsCompleted)return;
            // Immediate D3D11 context must only be used by its capture worker.
            try {
                var sender=(SpoutSender)typeof(VirtualCameraOutput).GetField("spout",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(output)!;
                result.TrySetResult(ReadTexture(sender));
            } catch(Exception ex) {result.TrySetException(ex);}
        };
        return result.Task;
    }
    private static int FindPortPair() {
        for(int attempt=0;attempt<100;attempt++) {
            int port=22000+Random.Shared.Next(500)*2;
            try {
                using var rtp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
                using var rtcp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port+1));
                return port;
            } catch(SocketException) { }
        }
        throw new Exception("No free fixture RTP/RTCP pair");
    }
}
