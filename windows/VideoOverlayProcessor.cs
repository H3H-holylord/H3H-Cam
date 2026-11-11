namespace S8Cam;

/// <summary>
/// Инженерный помощник монитора предпросмотра:
/// - Focus Peaking (подсветка резких контуров Sobel)
/// - Zebra Pattern (диагональная штриховка пересветов >= 95% IRE)
/// - False Color (тепловая карта экспозиции IRE по стандарту киномониторов ARRI/SmallHD)
/// </summary>
public static class VideoOverlayProcessor {
    public static void ProcessFrame(
        byte[] src,
        byte[] dst,
        int width,
        int height,
        bool enablePeaking,
        bool enableZebra,
        int peakThreshold = 55,
        bool enableFalseColor = false) {

        if (src == null || dst == null || width <= 2 || height <= 2) return;
        int totalBytes = Math.Min(src.Length, dst.Length);
        if (totalBytes < width * height * 4) return;

        Buffer.BlockCopy(src, 0, dst, 0, width * height * 4);

        if (!enablePeaking && !enableZebra && !enableFalseColor) return;

        int stride = width * 4;

        Parallel.For(1, height - 1, y => {
            int rowAbove = (y - 1) * stride;
            int rowCurrent = y * stride;
            int rowBelow = (y + 1) * stride;

            for (int x = 1; x < width - 1; x++) {
                int idx = rowCurrent + (x * 4);
                byte b = src[idx];
                byte g = src[idx + 1];
                byte r = src[idx + 2];

                int yVal = (r * 77 + g * 150 + b * 29) >> 8;

                // 0. False Color (IRE Exposure Map)
                if (enableFalseColor) {
                    int ire = (yVal * 100) / 255;
                    byte fr, fg, fb;
                    if (ire < 4) {
                        // Провал в глубокий чёрный: фиолетовый
                        fr = 148; fg = 56; fb = 225;
                    } else if (ire < 16) {
                        // Глубокие тени: синий
                        fr = 38; fg = 102; fb = 235;
                    } else if (ire is >= 38 and <= 43) {
                        // 18% Middle Gray: бирюзовый
                        fr = 30; fg = 195; fb = 210;
                    } else if (ire is >= 44 and <= 50) {
                        // Нижний ключ лица: изумрудно-зелёный
                        fr = 52; fg = 205; fb = 95;
                    } else if (ire is >= 52 and <= 60) {
                        // Идеальная экспозиция светлой кожи (~55 IRE): розовый (Skin IRE)
                        fr = 242; fg = 128; fb = 165;
                    } else if (ire is >= 84 and < 95) {
                        // Предпересвет (плечо кривой): янтарно-жёлтый
                        fr = 245; fg = 202; fb = 45;
                    } else if (ire >= 95) {
                        // Жёсткий пересвет (клиппинг): алый
                        fr = 245; fg = 42; fb = 42;
                    } else {
                        // Нейтральные полутона отображаются приглушённым монохромом для читаемости кадра
                        byte mono = (byte)yVal;
                        fr = mono; fg = mono; fb = mono;
                    }
                    dst[idx] = fb;
                    dst[idx + 1] = fg;
                    dst[idx + 2] = fr;
                }

                // 1. Zebra Pattern (Highlights >= 95%, ~240)
                if (enableZebra && yVal >= 240) {
                    if (((x + y) & 8) == 0) {
                        dst[idx] = 20;
                        dst[idx + 1] = 20;
                        dst[idx + 2] = 20;
                        continue;
                    }
                }

                // 2. Focus Peaking (Sharp gradient contours)
                if (enablePeaking) {
                    int idxL = rowCurrent + ((x - 1) * 4);
                    int idxR = rowCurrent + ((x + 1) * 4);
                    int yL = (src[idxL + 2] * 77 + src[idxL + 1] * 150 + src[idxL] * 29) >> 8;
                    int yR = (src[idxR + 2] * 77 + src[idxR + 1] * 150 + src[idxR] * 29) >> 8;

                    int idxU = rowAbove + (x * 4);
                    int idxD = rowBelow + (x * 4);
                    int yU = (src[idxU + 2] * 77 + src[idxU + 1] * 150 + src[idxU] * 29) >> 8;
                    int yD = (src[idxD + 2] * 77 + src[idxD + 1] * 150 + src[idxD] * 29) >> 8;

                    int gx = Math.Abs(yR - yL);
                    int gy = Math.Abs(yD - yU);
                    if (gx + gy > peakThreshold) {
                        dst[idx] = 40;
                        dst[idx + 1] = 255;
                        dst[idx + 2] = 60;
                    }
                }
            }
        });
    }
}
