using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S8Cam;

/// <summary>
/// High-resolution snapshot manager for capturing crystal-clear 4K UHD and native frames.
/// Saves lossless PNG images to %USERPROFILE%\Pictures\H3HCam.
/// </summary>
public static class SnapshotManager {
    public static string SnapshotDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "H3HCam");

    /// <summary>
    /// Captures a BGRA frame and saves it as a lossless PNG.
    /// If upscale4K is requested, runs the edge-adaptive SuperResolutionEngine to produce 3840x2160 output.
    /// </summary>
    public static (string FilePath, int Width, int Height, long FileSize) TakeSnapshot(
        byte[] bgraData,
        int width,
        int height,
        bool upscale4K = false,
        string? customDirectory = null) {

        if (bgraData == null || width <= 2 || height <= 2)
            throw new ArgumentException("Недопустимые размеры или пустой буфер кадра для снимка");

        var targetDir = customDirectory ?? SnapshotDirectory;
        Directory.CreateDirectory(targetDir);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var fileName = $"H3H_Snapshot_{timestamp}.png";
        var filePath = Path.Combine(targetDir, fileName);

        byte[] finalBuffer = bgraData;
        int finalW = width;
        int finalH = height;

        if (upscale4K && (width < 3840 || height < 2160)) {
            finalW = 3840;
            finalH = 2160;
            finalBuffer = new byte[finalW * finalH * 4];
            SuperResolutionEngine.UpscaleBgra(bgraData, width, height, finalBuffer, finalW, finalH, 0.25f);
        }

        var stride = finalW * 4;
        var bsrc = BitmapSource.Create(finalW, finalH, 96, 96, PixelFormats.Bgra32, null, finalBuffer, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bsrc));

        using (var fs = File.Create(filePath)) {
            encoder.Save(fs);
        }

        var fileInfo = new FileInfo(filePath);
        return (filePath, finalW, finalH, fileInfo.Length);
    }
}
