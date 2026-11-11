using System.Buffers;
using System.IO;

namespace S8Cam;

/// <summary>
/// Потокобезопасный генератор кадров «Умной шторки приватности» (Smart Privacy Slate)
/// для одновременной подачи в Spout2, OBS Virtual Camera и окно предпросмотра без участия UI-потока.
/// Поддерживает режимы: studio_card, blurred_freeze, custom_image, black.
/// </summary>
public static class SmartPrivacySlate {
    private sealed record CachedCard(int Width, int Height, string Theme, string Title, byte[] Bgra, byte[] Nv12);
    private static volatile CachedCard? lastCard;

    public static void RenderBgra(byte[] dstBgra, int width, int height, Settings settings, byte[]? lastLiveBgra = null) {
        if (dstBgra == null || width < 2 || height < 2 || dstBgra.Length < width * height * 4) return;
        string mode = (settings.PrivacySlateMode ?? "studio_card").ToLowerInvariant();

        if (mode == "black") {
            int total = width * height * 4;
            for (int i = 0; i < total; i += 4) {
                dstBgra[i] = 0;
                dstBgra[i + 1] = 0;
                dstBgra[i + 2] = 0;
                dstBgra[i + 3] = 255;
            }
            return;
        }

        if (mode == "custom_image" && !string.IsNullOrWhiteSpace(settings.PrivacySlateImage)) {
            var custom = StudioEffectsProcessor.GetCustomBackground(settings.PrivacySlateImage, width, height);
            if (custom != null && custom.Length >= width * height * 4) {
                Buffer.BlockCopy(custom, 0, dstBgra, 0, width * height * 4);
                DrawCenterBadge(dstBgra, width, height, settings.UiTheme == "nordic_atelier");
                return;
            }
        }

        if (mode == "blurred_freeze" && lastLiveBgra != null && lastLiveBgra.Length >= width * height * 4) {
            RenderBlurredFreezeBgra(lastLiveBgra, dstBgra, width, height, settings.UiTheme == "nordic_atelier");
            return;
        }

        var card = GetOrBuildStudioCard(width, height, settings.UiTheme ?? "cine_rig", settings.PrivacySlateTitle ?? "СКОРО ВЕРНУСЬ");
        Buffer.BlockCopy(card.Bgra, 0, dstBgra, 0, width * height * 4);
    }

    public static void RenderNv12(byte[] dstNv12, int width, int height, Settings settings, byte[]? lastLiveBgra = null) {
        if (dstNv12 == null || width < 2 || height < 2 || ((width | height) & 1) != 0) return;
        int neededNv12 = width * height * 3 / 2;
        if (dstNv12.Length < neededNv12) return;

        string mode = (settings.PrivacySlateMode ?? "studio_card").ToLowerInvariant();
        if (mode == "black") {
            dstNv12.AsSpan(0, width * height).Fill(16);
            dstNv12.AsSpan(width * height, width * height / 2).Fill(128);
            return;
        }

        if (mode == "studio_card" ||
            (mode == "blurred_freeze" && (lastLiveBgra == null || lastLiveBgra.Length < width * height * 4)) ||
            (mode == "custom_image" && string.IsNullOrWhiteSpace(settings.PrivacySlateImage))) {
            var card = GetOrBuildStudioCard(width, height, settings.UiTheme ?? "cine_rig", settings.PrivacySlateTitle ?? "СКОРО ВЕРНУСЬ");
            Buffer.BlockCopy(card.Nv12, 0, dstNv12, 0, neededNv12);
            return;
        }

        int neededBgra = width * height * 4;
        byte[] rented = ArrayPool<byte>.Shared.Rent(neededBgra);
        try {
            RenderBgra(rented, width, height, settings, lastLiveBgra);
            StudioEffectsProcessor.BgraToNv12(rented, dstNv12, width, height);
        } finally {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void RenderBlurredFreezeBgra(byte[] srcBgra, byte[] dstBgra, int width, int height, bool isLightTheme) {
        int smallW = Math.Max(16, width >> 2);
        int smallH = Math.Max(16, height >> 2);
        int smallBytes = smallW * smallH * 4;
        byte[] smallSrc = ArrayPool<byte>.Shared.Rent(smallBytes);
        byte[] smallBlur = ArrayPool<byte>.Shared.Rent(smallBytes);
        byte[] smallTmp = ArrayPool<byte>.Shared.Rent(smallBytes);
        try {
            int srcStride = width * 4;
            for (int sy = 0; sy < smallH; sy++) {
                int sRow = Math.Min(height - 1, sy << 2) * srcStride;
                int dRow = sy * smallW * 4;
                for (int sx = 0; sx < smallW; sx++) {
                    int sIdx = sRow + Math.Min(width - 1, sx << 2) * 4;
                    int dIdx = dRow + sx * 4;
                    smallSrc[dIdx] = srcBgra[sIdx];
                    smallSrc[dIdx + 1] = srcBgra[sIdx + 1];
                    smallSrc[dIdx + 2] = srcBgra[sIdx + 2];
                    smallSrc[dIdx + 3] = 255;
                }
            }
            StudioEffectsProcessor.FastBoxBlur(smallSrc, smallBlur, smallTmp, smallW, smallH, 12);
            StudioEffectsProcessor.FastBoxBlur(smallBlur, smallSrc, smallTmp, smallW, smallH, 10);

            Parallel.For(0, height, y => {
                float fy = (float)y * (smallH - 1) / Math.Max(1, height - 1);
                int y0 = (int)fy;
                int y1 = Math.Min(smallH - 1, y0 + 1);
                float wy = fy - y0;
                int dstRow = y * srcStride;
                int r0 = y0 * smallW * 4;
                int r1 = y1 * smallW * 4;

                for (int x = 0; x < width; x++) {
                    float fx = (float)x * (smallW - 1) / Math.Max(1, width - 1);
                    int x0 = (int)fx;
                    int x1 = Math.Min(smallW - 1, x0 + 1);
                    float wx = fx - x0;

                    int i00 = r0 + x0 * 4;
                    int i10 = r0 + x1 * 4;
                    int i01 = r1 + x0 * 4;
                    int i11 = r1 + x1 * 4;

                    int outIdx = dstRow + x * 4;
                    for (int c = 0; c < 3; c++) {
                        float top = smallSrc[i00 + c] + (smallSrc[i10 + c] - smallSrc[i00 + c]) * wx;
                        float bot = smallSrc[i01 + c] + (smallSrc[i11 + c] - smallSrc[i01 + c]) * wx;
                        float val = (top + (bot - top) * wy) * 0.46f;
                        dstBgra[outIdx + c] = (byte)Math.Clamp((int)val, 0, 255);
                    }
                    dstBgra[outIdx + 3] = 255;
                }
            });

            DrawCenterBadge(dstBgra, width, height, isLightTheme);
        } finally {
            ArrayPool<byte>.Shared.Return(smallSrc);
            ArrayPool<byte>.Shared.Return(smallBlur);
            ArrayPool<byte>.Shared.Return(smallTmp);
        }
    }

    private static CachedCard GetOrBuildStudioCard(int width, int height, string theme, string title) {
        string cleanTitle = string.IsNullOrWhiteSpace(title) ? "СКОРО ВЕРНУСЬ" : title.Trim().ToUpperInvariant();
        if (cleanTitle.Length > 34) cleanTitle = cleanTitle[..34];
        var existing = lastCard;
        if (existing != null && existing.Width == width && existing.Height == height &&
            existing.Theme == theme && existing.Title == cleanTitle) {
            return existing;
        }

        bool nordic = string.Equals(theme, "nordic_atelier", StringComparison.OrdinalIgnoreCase);
        var bgra = new byte[width * height * 4];

        // Базовый фон с мягкой радиальной виньеткой
        byte bgR = nordic ? (byte)236 : (byte)20;
        byte bgG = nordic ? (byte)234 : (byte)19;
        byte bgB = nordic ? (byte)228 : (byte)18;

        byte accR = nordic ? (byte)255 : (byte)245;
        byte accG = nordic ? (byte)85 : (byte)158;
        byte accB = nordic ? (byte)0 : (byte)11;

        byte inkR = nordic ? (byte)24 : (byte)244;
        byte inkG = nordic ? (byte)24 : (byte)241;
        byte inkB = nordic ? (byte)22 : (byte)234;

        byte subR = nordic ? (byte)118 : (byte)138;
        byte subG = nordic ? (byte)115 : (byte)132;
        byte subB = nordic ? (byte)108 : (byte)122;

        float cx = width * 0.5f;
        float cy = height * 0.5f;
        float invMaxR2 = 1.0f / Math.Max(1f, cx * cx + cy * cy);

        Parallel.For(0, height, y => {
            float dy = y - cy;
            int row = y * width * 4;
            for (int x = 0; x < width; x++) {
                float dx = x - cx;
                float dist = (dx * dx + dy * dy) * invMaxR2;
                float vig = nordic ? (1.0f - dist * 0.10f) : (1.0f - dist * 0.35f);
                // Лёгкое тёплое свечение в центре для Cine Rig
                float glow = !nordic ? MathF.Max(0f, 1.0f - dist * 3.2f) * 0.08f : 0f;

                int idx = row + x * 4;
                bgra[idx] = (byte)Math.Clamp((int)(bgB * vig + accB * glow), 0, 255);
                bgra[idx + 1] = (byte)Math.Clamp((int)(bgG * vig + accG * glow), 0, 255);
                bgra[idx + 2] = (byte)Math.Clamp((int)(bgR * vig + accR * glow), 0, 255);
                bgra[idx + 3] = 255;
            }
        });

        // Угловые режиссёрские метки видоискателя (Safe Area)
        int marginX = Math.Max(24, width / 18);
        int marginY = Math.Max(24, height / 14);
        int cornerLen = Math.Max(18, Math.Min(width, height) / 22);
        int thick = Math.Max(2, Math.Min(width, height) / 360);
        DrawCornerBrackets(bgra, width, height, marginX, marginY, width - marginX, height - marginY, cornerLen, thick, accR, accG, accB);

        // Центральный значок паузы
        DrawCenterBadge(bgra, width, height, nordic);

        // Заголовок крупным матричным шрифтом 5x7
        int scale = Math.Clamp(Math.Min(width, height) / 140, 2, 7);
        int titleY = (int)(height * 0.60f);
        DrawBitmapTextCentered(bgra, width, height, cleanTitle, titleY, scale, inkR, inkG, inkB);

        // Подпись статуса ниже
        int subScale = Math.Max(1, scale / 2);
        int subY = titleY + 9 * scale + Math.Max(8, height / 54);
        DrawBitmapTextCentered(bgra, width, height, "H3H CAM STUDIO  -  MIC & VIDEO MUTED", subY, subScale, subR, subG, subB);

        // Тонкая акцентная линия-разделитель под заголовком
        int barW = Math.Min(width / 3, Math.Max(80, cleanTitle.Length * 6 * scale / 2));
        int barY = titleY - Math.Max(10, height / 45);
        FillRect(bgra, width, height, (width - barW) / 2, barY, barW, Math.Max(2, thick), accR, accG, accB, 1.0f);

        int evenW = width & ~1;
        int evenH = height & ~1;
        var nv12 = new byte[Math.Max(6, evenW * evenH * 3 / 2)];
        if (evenW == width && evenH == height && evenW >= 2 && evenH >= 2) {
            StudioEffectsProcessor.BgraToNv12(bgra, nv12, width, height);
        }

        var created = new CachedCard(width, height, theme, cleanTitle, bgra, nv12);
        lastCard = created;
        return created;
    }

    private static void DrawCenterBadge(byte[] bgra, int width, int height, bool nordic) {
        int radius = Math.Clamp(Math.Min(width, height) / 13, 22, 72);
        int cx = width / 2;
        int cy = (int)(height * 0.43f);

        byte ringR = nordic ? (byte)255 : (byte)245;
        byte ringG = nordic ? (byte)85 : (byte)158;
        byte ringB = nordic ? (byte)0 : (byte)11;

        byte fillR = nordic ? (byte)247 : (byte)30;
        byte fillG = nordic ? (byte)246 : (byte)28;
        byte fillB = nordic ? (byte)242 : (byte)26;

        int rOut2 = radius * radius;
        int rIn = Math.Max(4, radius - Math.Max(3, radius / 10));
        int rIn2 = rIn * rIn;

        for (int y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++) {
            int dy = y - cy;
            int row = y * width * 4;
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++) {
                int dx = x - cx;
                int d2 = dx * dx + dy * dy;
                if (d2 > rOut2) continue;
                int idx = row + x * 4;
                if (d2 >= rIn2) {
                    bgra[idx] = ringB;
                    bgra[idx + 1] = ringG;
                    bgra[idx + 2] = ringR;
                } else {
                    bgra[idx] = (byte)((bgra[idx] + fillB * 3) >> 2);
                    bgra[idx + 1] = (byte)((bgra[idx + 1] + fillG * 3) >> 2);
                    bgra[idx + 2] = (byte)((bgra[idx + 2] + fillR * 3) >> 2);
                }
            }
        }

        // Две вертикальные полоски паузы ||
        int barW = Math.Max(4, radius / 4);
        int barH = Math.Max(10, (int)(radius * 0.82f));
        int gap = Math.Max(4, radius / 4);
        FillRect(bgra, width, height, cx - gap / 2 - barW, cy - barH / 2, barW, barH, ringR, ringG, ringB, 1.0f);
        FillRect(bgra, width, height, cx + (gap + 1) / 2, cy - barH / 2, barW, barH, ringR, ringG, ringB, 1.0f);
    }

    private static void DrawCornerBrackets(byte[] bgra, int w, int h, int x1, int y1, int x2, int y2, int len, int t, byte r, byte g, byte b) {
        // Верхний левый
        FillRect(bgra, w, h, x1, y1, len, t, r, g, b, 0.85f);
        FillRect(bgra, w, h, x1, y1, t, len, r, g, b, 0.85f);
        // Верхний правый
        FillRect(bgra, w, h, x2 - len, y1, len, t, r, g, b, 0.85f);
        FillRect(bgra, w, h, x2 - t, y1, t, len, r, g, b, 0.85f);
        // Нижний левый
        FillRect(bgra, w, h, x1, y2 - t, len, t, r, g, b, 0.85f);
        FillRect(bgra, w, h, x1, y2 - len, t, len, r, g, b, 0.85f);
        // Нижний правый
        FillRect(bgra, w, h, x2 - len, y2 - t, len, t, r, g, b, 0.85f);
        FillRect(bgra, w, h, x2 - t, y2 - len, t, len, r, g, b, 0.85f);
    }

    private static void FillRect(byte[] bgra, int w, int h, int rx, int ry, int rw, int rh, byte r, byte g, byte b, float alpha) {
        int x0 = Math.Clamp(rx, 0, w);
        int y0 = Math.Clamp(ry, 0, h);
        int x1 = Math.Clamp(rx + rw, 0, w);
        int y1 = Math.Clamp(ry + rh, 0, h);
        float invA = 1.0f - alpha;
        for (int y = y0; y < y1; y++) {
            int row = y * w * 4;
            for (int x = x0; x < x1; x++) {
                int idx = row + x * 4;
                bgra[idx] = (byte)(bgra[idx] * invA + b * alpha);
                bgra[idx + 1] = (byte)(bgra[idx + 1] * invA + g * alpha);
                bgra[idx + 2] = (byte)(bgra[idx + 2] * invA + r * alpha);
            }
        }
    }

    private static void DrawBitmapTextCentered(byte[] bgra, int w, int h, string text, int startY, int scale, byte r, byte g, byte b) {
        if (string.IsNullOrEmpty(text)) return;
        int charAdvance = 6 * scale;
        int totalW = text.Length * charAdvance - scale;
        int cursorX = (w - totalW) / 2;

        foreach (char ch in text) {
            uint[]? glyph = GetGlyph5x7(char.ToUpperInvariant(ch));
            if (glyph != null) {
                for (int gy = 0; gy < 7; gy++) {
                    uint rowBits = glyph[gy];
                    for (int gx = 0; gx < 5; gx++) {
                        if (((rowBits >> (4 - gx)) & 1u) != 0) {
                            FillRect(bgra, w, h, cursorX + gx * scale, startY + gy * scale, scale, scale, r, g, b, 1.0f);
                        }
                    }
                }
            }
            cursorX += charAdvance;
        }
    }

    // Встроенный студийный геометрический шрифт 5x7 (Латиница + Кириллица + Цифры + Знаки)
    private static uint[]? GetGlyph5x7(char c) => c switch {
        ' ' => [0, 0, 0, 0, 0, 0, 0],
        '-' or '—' or '–' => [0, 0, 0, 0b11111, 0, 0, 0],
        '·' or '.' => [0, 0, 0, 0b00100, 0, 0, 0],
        ':' => [0, 0b00100, 0, 0, 0, 0b00100, 0],
        '!' => [0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0, 0b00100],
        '&' => [0b01100, 0b10010, 0b10100, 0b01000, 0b10101, 0b10010, 0b01101],
        '/' => [0b00001, 0b00010, 0b00100, 0b00100, 0b01000, 0b10000, 0],
        '0' => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        '1' => [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        '2' => [0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111],
        '3' => [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        '4' => [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        '5' => [0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110],
        '6' => [0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110],
        '7' => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        '8' => [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        '9' => [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100],
        // Латиница
        'A' or 'А' => [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        'B' or 'В' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110],
        'C' or 'С' => [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
        'D' => [0b11100, 0b10010, 0b10001, 0b10001, 0b10001, 0b10010, 0b11100],
        'E' or 'Е' or 'Ё' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
        'F' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000],
        'G' => [0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110],
        'H' or 'Н' => [0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
        'I' => [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        'J' => [0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100],
        'K' or 'К' => [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        'L' => [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
        'M' or 'М' => [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
        'N' => [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
        'O' or 'О' => [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        'P' or 'Р' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
        'Q' => [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
        'R' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
        'S' => [0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110],
        'T' or 'Т' => [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
        'U' => [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
        'V' => [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
        'W' => [0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001],
        'X' or 'Х' => [0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001],
        'Y' => [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
        'Z' => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111],
        // Дополнительная кириллица
        'Б' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b11110],
        'Г' => [0b11111, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000],
        'Д' => [0b00110, 0b01010, 0b01010, 0b01010, 0b01010, 0b11111, 0b10001],
        'Ж' => [0b10101, 0b10101, 0b01110, 0b00100, 0b01110, 0b10101, 0b10101],
        'З' => [0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110],
        'И' => [0b10001, 0b10011, 0b10101, 0b10101, 0b11001, 0b10001, 0b10001],
        'Й' => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b10001],
        'Л' => [0b00111, 0b01001, 0b01001, 0b01001, 0b01001, 0b01001, 0b10001],
        'П' => [0b11111, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001],
        'У' => [0b10001, 0b10001, 0b10001, 0b01111, 0b00001, 0b10001, 0b01110],
        'Ф' => [0b00100, 0b01110, 0b10101, 0b10101, 0b01110, 0b00100, 0b00100],
        'Ц' => [0b10010, 0b10010, 0b10010, 0b10010, 0b10010, 0b11111, 0b00001],
        'Ч' => [0b10001, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b00001],
        'Ш' => [0b10101, 0b10101, 0b10101, 0b10101, 0b10101, 0b10101, 0b11111],
        'Щ' => [0b10101, 0b10101, 0b10101, 0b10101, 0b10101, 0b11111, 0b00001],
        'Ъ' => [0b11000, 0b01000, 0b01000, 0b01110, 0b01001, 0b01001, 0b01110],
        'Ы' => [0b10001, 0b10001, 0b10001, 0b11101, 0b10011, 0b10011, 0b11101],
        'Ь' => [0b10000, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b11110],
        'Э' => [0b01110, 0b10001, 0b00001, 0b00111, 0b00001, 0b10001, 0b01110],
        'Ю' => [0b10010, 0b10101, 0b10101, 0b11101, 0b10101, 0b10101, 0b10010],
        'Я' => [0b01111, 0b10001, 0b10001, 0b01111, 0b00101, 0b01001, 0b10001],
        _ => [0b01110, 0b10001, 0b00010, 0b00100, 0b00100, 0, 0b00100]
    };
}
