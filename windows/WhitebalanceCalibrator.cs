using System;

namespace S8Cam;

public sealed record WbCalibrationResult(
    bool Success,
    int EstimatedKelvin,
    int NearestPresetKelvin,
    float RedGain,
    float GreenGain,
    float BlueGain,
    byte AvgR,
    byte AvgG,
    byte AvgB,
    string Message
);

public static class WhitebalanceCalibrator {
    public static readonly int[] PresetKelvins = [2800, 3200, 4000, 5000, 5600, 6500, 7500];

    public static WbCalibrationResult CalibrateFromBgra(byte[] bgra, int width, int height, double centerRatio = 0.30) {
        if (bgra == null || bgra.Length < width * height * 4 || width <= 0 || height <= 0) {
            return new WbCalibrationResult(false, 0, 0, 1f, 1f, 1f, 0, 0, 0, "Кадр недоступен для анализа.");
        }

        int boxW = Math.Max(10, (int)(width * centerRatio));
        int boxH = Math.Max(10, (int)(height * centerRatio));
        int startX = (width - boxW) / 2;
        int startY = (height - boxH) / 2;

        long sumR = 0, sumG = 0, sumB = 0;
        int validCount = 0;
        int clippedCount = 0;
        int darkCount = 0;

        for (int y = startY; y < startY + boxH; y++) {
            int rowOffset = y * width * 4;
            for (int x = startX; x < startX + boxW; x++) {
                int idx = rowOffset + x * 4;
                byte b = bgra[idx];
                byte g = bgra[idx + 1];
                byte r = bgra[idx + 2];

                // Reject blown-out specular highlights / clipped glare
                if (r >= 248 || g >= 248 || b >= 248) {
                    clippedCount++;
                    continue;
                }

                // Check luminance
                double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (lum < 35.0) {
                    darkCount++;
                    continue;
                }

                // Reject wildly non-neutral objects (e.g. bright red or green book in center)
                double maxC = Math.Max((int)r, Math.Max((int)g, (int)b));
                double minC = Math.Max(1, Math.Min((int)r, Math.Min((int)g, (int)b)));
                if (maxC / minC > 3.0) {
                    continue;
                }

                sumR += r;
                sumG += g;
                sumB += b;
                validCount++;
            }
        }

        int totalSampled = boxW * boxH;
        if (validCount < 100 || validCount < totalSampled * 0.10) {
            if (clippedCount > totalSampled * 0.40) {
                return new WbCalibrationResult(false, 0, 0, 1f, 1f, 1f, 0, 0, 0,
                    "⚠️ Пересвет в центре кадра. Отойдите от прямого блика лампы и поднесите лист бумаги ровно.");
            }
            if (darkCount > totalSampled * 0.50) {
                return new WbCalibrationResult(false, 0, 0, 1f, 1f, 1f, 0, 0, 0,
                    "⚠️ Слишком темно. Направьте свет на лист бумаги в центре кадра.");
            }
            return new WbCalibrationResult(false, 0, 0, 1f, 1f, 1f, 0, 0, 0,
                "⚠️ Не удалось зафиксировать нейтральный лист в центре кадра. Поднесите белый лист ближе.");
        }

        byte avgR = (byte)Math.Clamp(sumR / validCount, 0, 255);
        byte avgG = (byte)Math.Clamp(sumG / validCount, 0, 255);
        byte avgB = (byte)Math.Clamp(sumB / validCount, 0, 255);

        // Convert sRGB to linear RGB (standard gamma expansion)
        double linR = SrgbToLinear(avgR);
        double linG = SrgbToLinear(avgG);
        double linB = SrgbToLinear(avgB);

        // Convert linear RGB to CIE XYZ
        double X = 0.4124564 * linR + 0.3575761 * linG + 0.1804375 * linB;
        double Y = 0.2126729 * linR + 0.7151522 * linG + 0.0721750 * linB;
        double Z = 0.0193339 * linR + 0.1191920 * linG + 0.9503041 * linB;
        double xyzSum = X + Y + Z;

        int estimatedKelvin = 5000;
        if (xyzSum > 0.001) {
            double cx = X / xyzSum;
            double cy = Y / xyzSum;
            double denom = 0.1858 - cy;
            if (Math.Abs(denom) > 0.0001) {
                double n = (cx - 0.3320) / denom;
                double cct = 449.0 * Math.Pow(n, 3) + 3525.0 * Math.Pow(n, 2) + 6823.3 * n + 5520.33;
                if (!double.IsNaN(cct) && !double.IsInfinity(cct)) {
                    estimatedKelvin = (int)Math.Clamp(Math.Round(cct), 2200, 8500);
                }
            }
        }

        // Find nearest supported camera preset
        int nearestPreset = FindNearestPreset(estimatedKelvin);

        // Fine-tuning gains to make neutral gray: G is reference
        float targetGray = (avgR + avgG + avgB) / 3f;
        float rGain = (float)Math.Clamp(targetGray / Math.Max(1f, (float)avgR), 0.60f, 1.60f);
        float gGain = (float)Math.Clamp(targetGray / Math.Max(1f, (float)avgG), 0.75f, 1.35f);
        float bGain = (float)Math.Clamp(targetGray / Math.Max(1f, (float)avgB), 0.60f, 1.60f);

        // Normalize relative to G
        rGain = (float)Math.Clamp(rGain / gGain, 0.60f, 1.80f);
        bGain = (float)Math.Clamp(bGain / gGain, 0.60f, 1.80f);
        gGain = 1.0f;

        string msg = $"✅ Откалибровано по белому листу: ~{estimatedKelvin} K (пресет {nearestPreset} K, R:{rGain:F2} B:{bGain:F2})";
        return new WbCalibrationResult(true, estimatedKelvin, nearestPreset, rGain, gGain, bGain, avgR, avgG, avgB, msg);
    }

    private static double SrgbToLinear(byte c) {
        double v = c / 255.0;
        return (v <= 0.04045) ? (v / 12.92) : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    public static int FindNearestPreset(int kelvin) {
        int best = PresetKelvins[0];
        int minDiff = Math.Abs(kelvin - best);
        for (int i = 1; i < PresetKelvins.Length; i++) {
            int diff = Math.Abs(kelvin - PresetKelvins[i]);
            if (diff < minDiff) {
                minDiff = diff;
                best = PresetKelvins[i];
            }
        }
        return best;
    }
}
