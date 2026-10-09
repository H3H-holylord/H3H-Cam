using S8Cam;

internal static class Nv12Tests {
    public static void Run() {
        var sampler=new PreviewSampler();
        var source=new byte[]{40,90,160,220,128,128};
        var pixels=sampler.SampleNv12(source,2,2,2,2);
        foreach(var (value,index) in new[]{40,90,160,220}.Select((v,i)=>(v,i))) {
            int gray=Math.Clamp((298*(value-16)+128)>>8,0,255);
            for(int c=0;c<3;c++)if(pixels[index*4+c]!=gray)throw new Exception("NV12 preview orientation/color mismatch");
            if(pixels[index*4+3]!=255)throw new Exception("NV12 preview alpha mismatch");
        }
        void Reject(Action action) {
            try {action();} catch(ArgumentException) {return;}
            throw new Exception("Invalid unsafe NV12 buffer was accepted");
        }
        Reject(()=>sampler.SampleNv12(new byte[14],3,3,2,2));
        Reject(()=>SuperResolutionEngine.UpscaleNv12(new byte[14],3,3,new byte[24],4,4));
        Reject(()=>SuperResolutionEngine.UpscaleNv12(new byte[6],2,2,new byte[23],4,4));
        Reject(()=>SuperResolutionEngine.UpscaleNv12(source,2,2,source,2,2));
        var upscale=new byte[24];
        SuperResolutionEngine.UpscaleNv12(source,2,2,upscale,4,4,0);
        if(upscale[0]!=40||upscale[15]!=220||upscale.Skip(16).Any(v=>v!=128))throw new Exception("NV12 edge/chroma scaling mismatch");
        Parallel.For(0,100,i=>{
            int width=i%2==0?64:80;
            var raw=Enumerable.Repeat((byte)(80+i%2),width*36*3/2).ToArray();
            var result=new byte[width*2*72*3/2];
            SuperResolutionEngine.UpscaleNv12(raw,width,36,result,width*2,72,0);
            if(result.Any(v=>v<79||v>81))throw new Exception("Concurrent NV12 cache dimensions/ownership mismatch");
        });
        Console.WriteLine("PASS NV12 preview/scaling · corner orientation · chroma · invalid buffers · concurrent sizes");
    }
}
