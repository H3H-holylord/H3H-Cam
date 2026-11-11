namespace S8Cam;

/// <summary>
/// Сверхбыстрый (< 0.25 мс) генератор студийных видеоскопов (Luma Waveform, RGB Parade, RGB Histogram)
/// в буфер BGRA32 (240x104) для вывода поверх режиссёрского монитора.
/// </summary>
public static class VideoScopesRenderer {
    public const int ScopeWidth = 240;
    public const int ScopeHeight = 104;

    public static void RenderScope(ReadOnlySpan<byte> frameBgra, int srcW, int srcH, string mode, Span<byte> outBgra) {
        int needed = ScopeWidth * ScopeHeight * 4;
        if (outBgra.Length < needed || srcW <= 0 || srcH <= 0 || frameBgra.Length < srcW * srcH * 4) return;

        // Полупрозрачная тёмно-графитовая подложка монитора с разметкой IRE (0, 25, 50, 75, 100)
        for (int y = 0; y < ScopeHeight; y++) {
            bool isGridLine = y == 4 || y == ScopeHeight - 5 || y == ScopeHeight / 2 ||
                              y == ScopeHeight / 4 || y == (ScopeHeight * 3) / 4;
            byte baseB = isGridLine ? (byte)46 : (byte)18;
            byte baseG = isGridLine ? (byte)44 : (byte)17;
            byte baseR = isGridLine ? (byte)42 : (byte)16;
            byte baseA = 222;
            int row = y * ScopeWidth * 4;
            for (int x = 0; x < ScopeWidth; x++) {
                if (isGridLine && (x & 2) != 0 && y != 4 && y != ScopeHeight - 5) {
                    outBgra[row + x * 4] = 18;
                    outBgra[row + x * 4 + 1] = 17;
                    outBgra[row + x * 4 + 2] = 16;
                    outBgra[row + x * 4 + 3] = baseA;
                } else {
                    outBgra[row + x * 4] = baseB;
                    outBgra[row + x * 4 + 1] = baseG;
                    outBgra[row + x * 4 + 2] = baseR;
                    outBgra[row + x * 4 + 3] = baseA;
                }
            }
        }

        switch (mode) {
            case "parade":
                RenderRgbParade(frameBgra, srcW, srcH, outBgra);
                break;
            case "histogram":
                RenderHistogram(frameBgra, srcW, srcH, outBgra);
                break;
            default:
                RenderLumaWaveform(frameBgra, srcW, srcH, outBgra);
                break;
        }
    }

    private static void RenderLumaWaveform(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst) {
        int stepY = Math.Max(2, srcH / 96);
        int padY = 4;
        int plotH = ScopeHeight - padY * 2 - 1;

        for (int sx = 0; sx < ScopeWidth; sx++) {
            int frameX = (sx * srcW) / ScopeWidth;
            for (int fy = 0; fy < srcH; fy += stepY) {
                int idx = (fy * srcW + frameX) * 4;
                int b = src[idx];
                int g = src[idx + 1];
                int r = src[idx + 2];
                int luma = (r * 77 + g * 150 + b * 29) >> 8;
                int sy = padY + plotH - ((luma * plotH) / 255);
                int outIdx = (sy * ScopeWidth + sx) * 4;
                // Фосфорно-лаймовое накопление с янтарным предупреждением на пиках
                if (luma >= 242) {
                    dst[outIdx] = (byte)Math.Min(255, dst[outIdx] + 20);
                    dst[outIdx + 1] = (byte)Math.Min(255, dst[outIdx + 1] + 55);
                    dst[outIdx + 2] = (byte)Math.Min(255, dst[outIdx + 2] + 95);
                } else {
                    dst[outIdx] = (byte)Math.Min(255, dst[outIdx] + 18);
                    dst[outIdx + 1] = (byte)Math.Min(255, dst[outIdx + 1] + 68);
                    dst[outIdx + 2] = (byte)Math.Min(255, dst[outIdx + 2] + 42);
                }
            }
        }
    }

    private static void RenderRgbParade(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst) {
        int colW = ScopeWidth / 3;
        int stepY = Math.Max(2, srcH / 84);
        int padY = 4;
        int plotH = ScopeHeight - padY * 2 - 1;

        for (int cx = 0; cx < colW - 2; cx++) {
            int frameX = (cx * srcW) / (colW - 2);
            for (int fy = 0; fy < srcH; fy += stepY) {
                int idx = (fy * srcW + frameX) * 4;
                byte b = src[idx];
                byte g = src[idx + 1];
                byte r = src[idx + 2];

                // R column
                int ry = padY + plotH - ((r * plotH) / 255);
                int rIdx = (ry * ScopeWidth + cx) * 4;
                dst[rIdx + 2] = (byte)Math.Min(255, dst[rIdx + 2] + 72);
                dst[rIdx + 1] = (byte)Math.Min(255, dst[rIdx + 1] + 18);

                // G column
                int gy = padY + plotH - ((g * plotH) / 255);
                int gIdx = (gy * ScopeWidth + colW + 1 + cx) * 4;
                dst[gIdx + 1] = (byte)Math.Min(255, dst[gIdx + 1] + 72);
                dst[gIdx] = (byte)Math.Min(255, dst[gIdx] + 18);

                // B column
                int by = padY + plotH - ((b * plotH) / 255);
                int bIdx = (by * ScopeWidth + colW * 2 + 2 + cx) * 4;
                dst[bIdx] = (byte)Math.Min(255, dst[bIdx] + 80);
                dst[bIdx + 1] = (byte)Math.Min(255, dst[bIdx + 1] + 32);
            }
        }
    }

    private static void RenderHistogram(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst) {
        Span<int> histR = stackalloc int[ScopeWidth];
        Span<int> histG = stackalloc int[ScopeWidth];
        Span<int> histB = stackalloc int[ScopeWidth];

        int stepX = Math.Max(2, srcW / 120);
        int stepY = Math.Max(2, srcH / 72);
        for (int y = 0; y < srcH; y += stepY) {
            int row = y * srcW * 4;
            for (int x = 0; x < srcW; x += stepX) {
                int idx = row + x * 4;
                histB[(src[idx] * (ScopeWidth - 1)) / 255]++;
                histG[(src[idx + 1] * (ScopeWidth - 1)) / 255]++;
                histR[(src[idx + 2] * (ScopeWidth - 1)) / 255]++;
            }
        }

        int peak = 1;
        for (int i = 2; i < ScopeWidth - 2; i++) {
            peak = Math.Max(peak, Math.Max(histR[i], Math.Max(histG[i], histB[i])));
        }

        int padY = 4;
        int plotH = ScopeHeight - padY * 2;
        for (int x = 0; x < ScopeWidth; x++) {
            int hr = Math.Min(plotH, (histR[x] * plotH) / peak);
            int hg = Math.Min(plotH, (histG[x] * plotH) / peak);
            int hb = Math.Min(plotH, (histB[x] * plotH) / peak);

            for (int h = 0; h < plotH; h++) {
                int sy = ScopeHeight - padY - 1 - h;
                int outIdx = (sy * ScopeWidth + x) * 4;
                if (h < hb) dst[outIdx] = (byte)Math.Min(255, dst[outIdx] + 150);
                if (h < hg) dst[outIdx + 1] = (byte)Math.Min(255, dst[outIdx + 1] + 150);
                if (h < hr) dst[outIdx + 2] = (byte)Math.Min(255, dst[outIdx + 2] + 150);
            }
        }
    }
}
