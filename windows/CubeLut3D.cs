using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace S8Cam;

/// <summary>
/// Кинематографический 3D LUT (.cube) движок с трилинейной интерполяцией на CPU и упаковкой RGBA8 для D3D11 Texture3D.
/// Включает 3 встроенных студийных 3D LUT пресета (Kodak 2383, ARRI Warm 709, Nordic Clean).
/// </summary>
public sealed class CubeLut3D {
    public string Title { get; }
    public string SourceKey { get; }
    public int Size { get; }
    // Плоский массив float RGB [Size * Size * Size * 3], индексация: ((b * Size + g) * Size + r) * 3
    private readonly float[] table;
    // Готовый RGBA8 буфер [Size * Size * Size * 4] для загрузки в ID3D11Texture3D
    public byte[] Rgba3DData { get; }

    public CubeLut3D(string sourceKey, string title, int size, float[] rgbTable) {
        if (size is < 2 or > 65) throw new ArgumentOutOfRangeException(nameof(size), "LUT 3D size must be 2..65");
        if (rgbTable.Length != size * size * size * 3)
            throw new ArgumentException("Invalid RGB table size for 3D LUT");

        SourceKey = sourceKey;
        Title = title;
        Size = size;
        table = rgbTable;
        Rgba3DData = new byte[size * size * size * 4];
        int pixels = size * size * size;
        for (int i = 0; i < pixels; i++) {
            Rgba3DData[i * 4] = (byte)Math.Clamp((int)MathF.Round(rgbTable[i * 3] * 255f), 0, 255);
            Rgba3DData[i * 4 + 1] = (byte)Math.Clamp((int)MathF.Round(rgbTable[i * 3 + 1] * 255f), 0, 255);
            Rgba3DData[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round(rgbTable[i * 3 + 2] * 255f), 0, 255);
            Rgba3DData[i * 4 + 3] = 255;
        }
    }

    private static volatile CubeLut3D? cachedLut;

    public static CubeLut3D? GetOrLoad(string? pathOrPreset) {
        if (string.IsNullOrWhiteSpace(pathOrPreset) || pathOrPreset == "none") return null;
        var current = cachedLut;
        if (current != null && string.Equals(current.SourceKey, pathOrPreset, StringComparison.OrdinalIgnoreCase)) {
            return current;
        }
        try {
            CubeLut3D loaded = pathOrPreset.ToLowerInvariant() switch {
                "builtin:kodak2383" => CreateBuiltInKodak2383(),
                "builtin:arri_warm" => CreateBuiltInArriWarm(),
                "builtin:nordic_clean" => CreateBuiltInNordicClean(),
                _ => File.Exists(pathOrPreset) ? ParseCubeFile(pathOrPreset) : null!
            };
            if (loaded != null) {
                cachedLut = loaded;
            }
            return loaded;
        } catch {
            return null;
        }
    }

    public static CubeLut3D ParseCubeText(string cubeContent, string sourceKey = "memory.cube") {
        string title = Path.GetFileNameWithoutExtension(sourceKey);
        int size = 0;
        float domainMinR = 0f, domainMinG = 0f, domainMinB = 0f;
        float domainMaxR = 1f, domainMaxG = 1f, domainMaxB = 1f;
        float[]? data = null;
        int writeIdx = 0;

        using var reader = new StringReader(cubeContent);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null) {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase)) {
                int q1 = line.IndexOf('"');
                int q2 = line.LastIndexOf('"');
                if (q1 >= 0 && q2 > q1) title = line[(q1 + 1)..q2];
                continue;
            }
            if (line.StartsWith("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sz)) {
                    if (sz is < 2 or > 65) throw new InvalidDataException($"Unsupported LUT_3D_SIZE: {sz}");
                    size = sz;
                    data = new float[size * size * size * 3];
                }
                continue;
            }
            if (line.StartsWith("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException("Only 3D .cube LUT files are supported");
            }
            if (line.StartsWith("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase)) {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4) {
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMinR);
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMinG);
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMinB);
                }
                continue;
            }
            if (line.StartsWith("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase)) {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4) {
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMaxR);
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMaxG);
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out domainMaxB);
                }
                continue;
            }
            if (char.IsLetter(line[0])) continue;

            if (data == null) continue;
            var nums = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length >= 3 && writeIdx + 3 <= data.Length &&
                float.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
                float.TryParse(nums[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
                float.TryParse(nums[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) {
                float spanR = Math.Max(1e-5f, domainMaxR - domainMinR);
                float spanG = Math.Max(1e-5f, domainMaxG - domainMinG);
                float spanB = Math.Max(1e-5f, domainMaxB - domainMinB);
                data[writeIdx++] = Math.Clamp((r - domainMinR) / spanR, 0f, 1f);
                data[writeIdx++] = Math.Clamp((g - domainMinG) / spanG, 0f, 1f);
                data[writeIdx++] = Math.Clamp((b - domainMinB) / spanB, 0f, 1f);
            }
        }

        if (size < 2 || data == null || writeIdx != data.Length) {
            throw new InvalidDataException($"Incomplete .cube 3D LUT data ({writeIdx / 3} entries)");
        }
        return new CubeLut3D(sourceKey, title, size, data);
    }

    public static CubeLut3D ParseCubeFile(string filePath) =>
        ParseCubeText(File.ReadAllText(filePath), filePath);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPixel(ref byte r, ref byte g, ref byte b, float strength) {
        if (strength <= 0.001f) return;
        strength = Math.Clamp(strength, 0f, 1f);

        int maxIdx = Size - 1;
        float scale = maxIdx / 255.0f;
        float rf = r * scale;
        float gf = g * scale;
        float bf = b * scale;

        int r0 = (int)rf; int r1 = r0 < maxIdx ? r0 + 1 : maxIdx; float dr = rf - r0;
        int g0 = (int)gf; int g1 = g0 < maxIdx ? g0 + 1 : maxIdx; float dg = gf - g0;
        int b0 = (int)bf; int b1 = b0 < maxIdx ? b0 + 1 : maxIdx; float db = bf - b0;

        int strideG = Size * 3;
        int strideB = Size * strideG;

        int i000 = b0 * strideB + g0 * strideG + r0 * 3;
        int i100 = b0 * strideB + g0 * strideG + r1 * 3;
        int i010 = b0 * strideB + g1 * strideG + r0 * 3;
        int i110 = b0 * strideB + g1 * strideG + r1 * 3;
        int i001 = b1 * strideB + g0 * strideG + r0 * 3;
        int i101 = b1 * strideB + g0 * strideG + r1 * 3;
        int i011 = b1 * strideB + g1 * strideG + r0 * 3;
        int i111 = b1 * strideB + g1 * strideG + r1 * 3;

        var t = table;
        for (int c = 0; c < 3; c++) {
            float c00 = t[i000 + c] + (t[i100 + c] - t[i000 + c]) * dr;
            float c10 = t[i010 + c] + (t[i110 + c] - t[i010 + c]) * dr;
            float c01 = t[i001 + c] + (t[i101 + c] - t[i001 + c]) * dr;
            float c11 = t[i011 + c] + (t[i111 + c] - t[i011 + c]) * dr;
            float c0 = c00 + (c10 - c00) * dg;
            float c1 = c01 + (c11 - c01) * dg;
            float mapped = (c0 + (c1 - c0) * db) * 255.0f;

            if (c == 0) {
                r = (byte)Math.Clamp((int)MathF.Round(r + (mapped - r) * strength), 0, 255);
            } else if (c == 1) {
                g = (byte)Math.Clamp((int)MathF.Round(g + (mapped - g) * strength), 0, 255);
            } else {
                b = (byte)Math.Clamp((int)MathF.Round(b + (mapped - b) * strength), 0, 255);
            }
        }
    }

    public void ApplyBgraFrame(byte[] bgra, int width, int height, float strength) {
        if (strength <= 0.001f || bgra.Length < width * height * 4) return;
        int stride = width * 4;
        Parallel.For(0, height, y => {
            int row = y * stride;
            for (int x = 0; x < width; x++) {
                int idx = row + x * 4;
                byte b = bgra[idx];
                byte g = bgra[idx + 1];
                byte r = bgra[idx + 2];
                ApplyPixel(ref r, ref g, ref b, strength);
                bgra[idx] = b;
                bgra[idx + 1] = g;
                bgra[idx + 2] = r;
            }
        });
    }

    private static float SmoothContrast(float x, float contrast) {
        float centered = x - 0.5f;
        return Math.Clamp(0.5f + centered * contrast - 0.12f * centered * centered * centered, 0f, 1f);
    }

    public static CubeLut3D CreateBuiltInKodak2383() {
        const int sz = 17;
        var data = new float[sz * sz * sz * 3];
        int p = 0;
        for (int bi = 0; bi < sz; bi++) {
            float b = bi / (float)(sz - 1);
            for (int gi = 0; gi < sz; gi++) {
                float g = gi / (float)(sz - 1);
                for (int ri = 0; ri < sz; ri++) {
                    float r = ri / (float)(sz - 1);
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    float shadowWeight = MathF.Pow(1f - luma, 1.8f);
                    float highlightWeight = MathF.Pow(luma, 1.6f);

                    float nr = SmoothContrast(r, 1.14f) - 0.035f * shadowWeight + 0.045f * highlightWeight;
                    float ng = SmoothContrast(g, 1.12f) + 0.010f * shadowWeight + 0.020f * highlightWeight;
                    float nb = SmoothContrast(b, 1.10f) + 0.050f * shadowWeight - 0.045f * highlightWeight;

                    data[p++] = Math.Clamp(nr, 0f, 1f);
                    data[p++] = Math.Clamp(ng, 0f, 1f);
                    data[p++] = Math.Clamp(nb, 0f, 1f);
                }
            }
        }
        return new CubeLut3D("builtin:kodak2383", "Kodak 2383 Print Film", sz, data);
    }

    public static CubeLut3D CreateBuiltInArriWarm() {
        const int sz = 17;
        var data = new float[sz * sz * sz * 3];
        int p = 0;
        for (int bi = 0; bi < sz; bi++) {
            float b = bi / (float)(sz - 1);
            for (int gi = 0; gi < sz; gi++) {
                float g = gi / (float)(sz - 1);
                for (int ri = 0; ri < sz; ri++) {
                    float r = ri / (float)(sz - 1);
                    // Мягкий кинематографический roll-off светов
                    float RollOff(float v) => v > 0.75f ? 0.75f + (v - 0.75f) * 0.78f : v;
                    float nr = RollOff(SmoothContrast(r, 1.07f) * 1.04f + 0.012f);
                    float ng = RollOff(SmoothContrast(g, 1.06f) * 1.01f + 0.005f);
                    float nb = RollOff(SmoothContrast(b, 1.05f) * 0.94f);

                    data[p++] = Math.Clamp(nr, 0f, 1f);
                    data[p++] = Math.Clamp(ng, 0f, 1f);
                    data[p++] = Math.Clamp(nb, 0f, 1f);
                }
            }
        }
        return new CubeLut3D("builtin:arri_warm", "ARRI Alexa Rec.709 Warm", sz, data);
    }

    public static CubeLut3D CreateBuiltInNordicClean() {
        const int sz = 17;
        var data = new float[sz * sz * sz * 3];
        int p = 0;
        for (int bi = 0; bi < sz; bi++) {
            float b = bi / (float)(sz - 1);
            for (int gi = 0; gi < sz; gi++) {
                float g = gi / (float)(sz - 1);
                for (int ri = 0; ri < sz; ri++) {
                    float r = ri / (float)(sz - 1);
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    // Лёгкий подъём точки чёрного (матовая бумага) и прохладная чистота светов
                    float nr = 0.02f + SmoothContrast(r, 1.05f) * 0.95f;
                    float ng = 0.02f + SmoothContrast(g, 1.06f) * 0.97f;
                    float nb = 0.025f + SmoothContrast(b, 1.08f) * 0.99f;
                    // Деликатное снижение перенасыщенности
                    nr = luma + (nr - luma) * 0.88f;
                    ng = luma + (ng - luma) * 0.88f;
                    nb = luma + (nb - luma) * 0.92f;

                    data[p++] = Math.Clamp(nr, 0f, 1f);
                    data[p++] = Math.Clamp(ng, 0f, 1f);
                    data[p++] = Math.Clamp(nb, 0f, 1f);
                }
            }
        }
        return new CubeLut3D("builtin:nordic_clean", "Nordic Atelier Daylight", sz, data);
    }
}
