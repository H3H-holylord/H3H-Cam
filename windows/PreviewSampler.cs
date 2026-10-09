namespace S8Cam;

/// <summary>Reusable BGRA preview from the existing output decoder, before studio effects.
/// Fixed-point bilinear sampling keeps preview work small without a second video decoder.</summary>
public sealed class PreviewSampler {
    private byte[]? pixels;
    private int[]? columns;
    private int sourceWidth, previewWidth;
    public unsafe byte[] Sample(byte[] source, int width, int height, int targetWidth, int targetHeight) {
        if (source.Length < checked(width*height*4) || width < 1 || height < 1 || targetWidth < 1 || targetHeight < 1)
            throw new ArgumentException("Invalid preview dimensions/buffer");
        int size = checked(targetWidth*targetHeight*4);
        if (pixels == null || pixels.Length != size) pixels = new byte[size];
        if (columns == null || sourceWidth != width || previewWidth != targetWidth) {
            columns = new int[targetWidth*3]; sourceWidth=width; previewWidth=targetWidth;
            for (int x=0; x<targetWidth; x++) {
                long sx=Math.Clamp(((2L*x+1)*width*128/targetWidth)-128, 0, (width-1L)*256);
                columns[x*3]=(int)(sx>>8)*4;
                columns[x*3+1]=Math.Min((int)(sx>>8)+1,width-1)*4;
                columns[x*3+2]=(int)(sx&255);
            }
        }
        fixed (byte* src = source, dst = pixels) {
            for (int y=0; y<targetHeight; y++) {
                long sy = Math.Clamp(((2L*y+1)*height*128/targetHeight)-128, 0, (height-1L)*256);
                int y0=(int)(sy>>8), y1=Math.Min(y0+1, height-1), fy=(int)(sy&255);
                for (int x=0; x<targetWidth; x++) {
                    int x0=columns[x*3], x1=columns[x*3+1], fx=columns[x*3+2];
                    byte* a=src+y0*width*4+x0; byte* b=src+y0*width*4+x1;
                    byte* c=src+y1*width*4+x0; byte* d=src+y1*width*4+x1;
                    byte* target=dst+(y*targetWidth+x)*4;
                    for (int channel=0; channel<4; channel++) {
                        int top=a[channel]*(256-fx)+b[channel]*fx;
                        int bottom=c[channel]*(256-fx)+d[channel]*fx;
                        target[channel]=(byte)((top*(256-fy)+bottom*fy+32768)>>16);
                    }
                }
            }
        }
        return pixels;
    }

    public unsafe byte[] SampleNv12(byte[] source, int width, int height, int targetWidth, int targetHeight) {
        if (width < 2 || height < 2 || (width&1)!=0 || (height&1)!=0 || targetWidth < 1 || targetHeight < 1 ||
            source.Length < checked(width*height*3/2))
            throw new ArgumentException("Invalid preview dimensions/buffer");
        int size = checked(targetWidth*targetHeight*4);
        if (pixels == null || pixels.Length != size) pixels = new byte[size];

        fixed (byte* src = source, dst = pixels) {
            int uvOffset = width * height;
            for (int y = 0; y < targetHeight; y++) {
                int sy = Math.Clamp(y * height / targetHeight, 0, height - 1);
                int yRow = sy * width;
                int uvRow = uvOffset + (sy >> 1) * width;
                int dstRow = y * targetWidth * 4;

                for (int x = 0; x < targetWidth; x++) {
                    int sx = Math.Clamp(x * width / targetWidth, 0, width - 1);
                    int yVal = src[yRow + sx];
                    int uvIdx = uvRow + (sx & ~1);
                    int u = src[uvIdx] - 128;
                    int v = src[uvIdx + 1] - 128;

                    int c = Math.Max(0, yVal - 16);
                    int r = (298 * c + 409 * v + 128) >> 8;
                    int g = (298 * c - 100 * u - 208 * v + 128) >> 8;
                    int b = (298 * c + 516 * u + 128) >> 8;

                    int dIdx = dstRow + x * 4;
                    dst[dIdx] = (byte)Math.Clamp(b, 0, 255);
                    dst[dIdx + 1] = (byte)Math.Clamp(g, 0, 255);
                    dst[dIdx + 2] = (byte)Math.Clamp(r, 0, 255);
                    dst[dIdx + 3] = 255;
                }
            }
        }
        return pixels;
    }
}
