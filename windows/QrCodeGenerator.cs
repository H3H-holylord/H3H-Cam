using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S8Cam;

public static class QrCodeGenerator {
    public static WriteableBitmap GenerateBitmap(string text, int scale = 8, int border = 4) {
        if (scale is < 1 or > 32 || border is < 0 or > 16) throw new ArgumentOutOfRangeException();
        using var data = QRCoder.QRCodeGenerator.GenerateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        // QRCoder includes the mandatory four-module quiet zone in its matrix.
        var matrix = data.ModuleMatrix;
        var size = matrix.Count - 8;
        border = Math.Max(4, border);
        var imageSize = (size + 2 * border) * scale;
        var pixels = new uint[imageSize * imageSize];
        Array.Fill(pixels, 0xFFFFFFFF);
        for (var row = 0; row < size; row++) for (var col = 0; col < size; col++) {
            if (!matrix[row + 4][col + 4]) continue;
            for (var y = 0; y < scale; y++) for (var x = 0; x < scale; x++)
                pixels[((row + border) * scale + y) * imageSize + (col + border) * scale + x] = 0xFF000000;
        }
        var bitmap = new WriteableBitmap(imageSize, imageSize, 96, 96, PixelFormats.Bgr32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, imageSize, imageSize), pixels, imageSize * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
