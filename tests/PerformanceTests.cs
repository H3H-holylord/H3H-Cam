using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using S8Cam;

internal static class PerformanceTests {
    public static void VerifyConversion() {
        var random = new Random(1701);
        foreach (var (w, h) in new[] { (2,2), (14,6), (18,10), (960,540), (1920,1080) }) {
            var input = new byte[w*h*4]; random.NextBytes(input);
            var expected = new byte[w*h*3/2]; var actual = new byte[expected.Length];
            ReferenceConversion(input, expected, w, h);
            StudioEffectsProcessor.BgraToNv12(input, actual, w, h);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception($"NV12 differs at {w}x{h}");
        }
        var sampler = new PreviewSampler();
        byte[] small = [0,0,0,255, 40,40,40,255, 80,80,80,255, 120,120,120,255];
        var pixel = sampler.Sample(small, 2,2,1,1);
        if (!pixel.AsSpan().SequenceEqual(new byte[] {60,60,60,255})) throw new Exception("Preview averaging/color mismatch");
        var full = sampler.Sample(small, 2,2,2,2);
        if (!full.AsSpan().SequenceEqual(small)) throw new Exception("Preview identity mismatch");
        foreach (var profile in new[] {"none","clean","warm","cold","teal_orange","noir"}) {
            foreach (var saturation in new[] {1.0,1.35}) {
                var input=new byte[64*48*4]; random.NextBytes(input);
                var expected=new byte[input.Length]; var actual=new byte[input.Length];
                ReferenceColor(input,expected,64,48,profile,.07,1.14,saturation,1.15f,.9f,1.03f);
                StudioEffectsProcessor.ApplyEffects(input,actual,64,48,"none",colorProfile:profile,brightness:.07,contrast:1.14,saturation:saturation,wbR:1.15f,wbG:.9f,wbB:1.03f);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception("Color LUT differs: "+profile);
                StudioEffectsProcessor.ApplyEffects(input,input,64,48,"none",colorProfile:profile,brightness:.07,contrast:1.14,saturation:saturation,wbR:1.15f,wbG:.9f,wbB:1.03f);
                if (!input.AsSpan().SequenceEqual(expected)) throw new Exception("In-place color differs: "+profile);
            }
        }
        Console.WriteLine("PASS color LUT · profiles/WB/sliders · exact bytes · in-place · saturation fallback");
        Console.WriteLine("PASS NV12 · identical Y/UV, SIMD tail, 2x2 and 1080p");
    }
    public static unsafe void Run() {
        VerifyConversion();
        const int w=1920, h=1080, count=90;
        var input = new byte[w*h*4]; new Random(1701).NextBytes(input);
        var output = new byte[w*h*3/2];
        var newColorBuffer = new byte[input.Length];
        Measure("Clean color original", () => ReferenceColor(input, newColorBuffer, w,h,"clean",0,1,1,1,1,1),count);
        Measure("Clean color LUT", () => StudioEffectsProcessor.ApplyEffects(input,newColorBuffer,w,h,"none",colorProfile:"clean"),count);
        Measure("BGRA→NV12 original", () => ReferenceConversion(input, output, w, h), count);
        Measure("BGRA→NV12 optimized", () => StudioEffectsProcessor.BgraToNv12(input, output, w, h), count);
        using var map = MemoryMappedFile.CreateNew(null, output.Length);
        using var view = map.CreateViewAccessor();
        Measure("Shared-memory WriteArray", () => view.WriteArray(0, output, 0, output.Length), count);
        Measure("Shared-memory memcpy", () => {
            byte* dst = null; view.SafeMemoryMappedViewHandle.AcquirePointer(ref dst);
            try { fixed (byte* src = output) Buffer.MemoryCopy(src, dst + view.PointerOffset, output.Length, output.Length); }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }, count);
        var readback = new byte[output.Length]; view.ReadArray(0, readback, 0, readback.Length);
        if (!readback.AsSpan().SequenceEqual(output)) throw new Exception("Shared-memory copy differs");
    }
    private static void Measure(string label, Action action, int count) {
        for (int i=0; i<10; i++) action();
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime.TotalMilliseconds;
        var timer=Stopwatch.StartNew();
        for (int i=0; i<count; i++) action();
        timer.Stop();
        Console.WriteLine($"{label}: {timer.Elapsed.TotalMilliseconds/count:F3} ms/frame wall; {(process.TotalProcessorTime.TotalMilliseconds-cpu)/count:F3} ms/frame CPU");
    }
    private static void ReferenceColor(byte[] src,byte[] dst,int w,int h,string profile,double brightness,double contrast,double saturation,float wbR,float wbG,float wbB) {
        Parallel.For(0,h,y => {
            for (int x=0; x<w; x++) {
                int i=(y*w+x)*4; byte b=src[i],g=src[i+1],r=src[i+2];
                StudioEffectsProcessor.ApplyPixelColor(ref r,ref g,ref b,profile,brightness,contrast,saturation,wbR,wbG,wbB);
                dst[i]=b; dst[i+1]=g; dst[i+2]=r; dst[i+3]=255;
            }
        });
    }
    private static void ReferenceConversion(byte[] bgra, byte[] nv12, int w, int h) {
        int ySize = w * h;
        int uvOffset = ySize;
        int halfW = w / 2;
        int halfH = h / 2;

        Parallel.For(0, h, y => {
            int yRow = y * w;
            int bgraRow = yRow * 4;
            for (int x = 0; x < w; x++) {
                int bIdx = bgraRow + (x * 4);
                int b = bgra[bIdx];
                int g = bgra[bIdx + 1];
                int r = bgra[bIdx + 2];
                int yVal = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[yRow + x] = (byte)Math.Clamp(yVal, 0, 255);
            }
        });

        Parallel.For(0, halfH, uvY => {
            int uvRow = uvOffset + uvY * w;
            int y0 = uvY * 2;
            int y1 = y0 + 1;
            int bgraRow0 = y0 * w * 4;
            int bgraRow1 = (y1 < h ? y1 : y0) * w * 4;

            for (int uvX = 0; uvX < halfW; uvX++) {
                int x0 = uvX * 2;
                int x1 = x0 + 1 < w ? x0 + 1 : x0;

                int i00 = bgraRow0 + x0 * 4;
                int i01 = bgraRow0 + x1 * 4;
                int i10 = bgraRow1 + x0 * 4;
                int i11 = bgraRow1 + x1 * 4;

                int rAvg = (bgra[i00 + 2] + bgra[i01 + 2] + bgra[i10 + 2] + bgra[i11 + 2] + 2) >> 2;
                int gAvg = (bgra[i00 + 1] + bgra[i01 + 1] + bgra[i10 + 1] + bgra[i11 + 1] + 2) >> 2;
                int bAvg = (bgra[i00] + bgra[i01] + bgra[i10] + bgra[i11] + 2) >> 2;

                int u = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                int v = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;

                int uvIdx = uvRow + uvX * 2;
                nv12[uvIdx] = (byte)Math.Clamp(u, 0, 255);
                nv12[uvIdx + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        });
    }

}
