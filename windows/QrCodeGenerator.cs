using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S8Cam;

/// <summary>
/// Compact, zero-dependency pure C# QR Code generator (ISO/IEC 18004 compliant).
/// Generates crisp, scalable WPF WriteableBitmaps for quick mobile Wi-Fi pairing.
/// </summary>
public static class QrCodeGenerator {
    // Galois Field GF(256) tables for QR Reed-Solomon
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCodeGenerator() {
        int val = 1;
        for (int i = 0; i < 255; i++) {
            Exp[i] = (byte)val;
            Exp[i + 255] = (byte)val;
            Log[val] = (byte)i;
            val <<= 1;
            if ((val & 0x100) != 0) val ^= 0x11D;
        }
    }

    private static byte GfMul(byte a, byte b) {
        if (a == 0 || b == 0) return 0;
        return Exp[Log[a] + Log[b]];
    }

    public static WriteableBitmap GenerateBitmap(string text, int scale = 8, int border = 4) {
        bool[,] modules = Encode(text);
        int size = modules.GetLength(0);
        int imgSize = (size + border * 2) * scale;
        var bmp = new WriteableBitmap(imgSize, imgSize, 96, 96, PixelFormats.Bgr32, null);
        var pixels = new uint[imgSize * imgSize];

        // Fill background (#0C1421 dark studio theme or crisp white #FFFFFF)
        uint bg = 0xFFFFFFFF;
        uint fg = 0xFF0C1421;
        Array.Fill(pixels, bg);

        for (int r = 0; r < size; r++) {
            for (int c = 0; c < size; c++) {
                if (modules[r, c]) {
                    int startY = (r + border) * scale;
                    int startX = (c + border) * scale;
                    for (int y = 0; y < scale; y++) {
                        int row = (startY + y) * imgSize;
                        for (int x = 0; x < scale; x++) {
                            pixels[row + startX + x] = fg;
                        }
                    }
                }
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, imgSize, imgSize), pixels, imgSize * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    public static bool[,] Encode(string text) {
        byte[] dataBytes = Encoding.UTF8.GetBytes(text);
        // Determine smallest QR version (Version 1 to 6)
        int version = 1;
        int totalCodewords = 26;
        int dataCodewords = 19;
        int ecCodewords = 7;

        if (dataBytes.Length > 17) {
            version = 2; totalCodewords = 44; dataCodewords = 34; ecCodewords = 10;
        }
        if (dataBytes.Length > 32) {
            version = 3; totalCodewords = 70; dataCodewords = 55; ecCodewords = 15;
        }
        if (dataBytes.Length > 53) {
            version = 4; totalCodewords = 100; dataCodewords = 80; ecCodewords = 20;
        }
        if (dataBytes.Length > 78) {
            version = 5; totalCodewords = 134; dataCodewords = 108; ecCodewords = 26;
        }
        if (dataBytes.Length > 106) {
            version = 6; totalCodewords = 172; dataCodewords = 136; ecCodewords = 36;
        }
        if (dataBytes.Length > 134) {
            Array.Resize(ref dataBytes, 134);
        }

        // Build data bitstream (Mode 8-bit byte: 0100 + count + data + terminator)
        var bits = new List<bool>();
        AddBits(bits, 4, 4); // Byte mode (0100)
        AddBits(bits, dataBytes.Length, 8); // Character count
        foreach (byte b in dataBytes) AddBits(bits, b, 8);

        // Terminator (up to 4 zeroes)
        for (int i = 0; i < 4 && bits.Count < dataCodewords * 8; i++) bits.Add(false);
        while (bits.Count % 8 != 0) bits.Add(false);

        // Pad codewords (0xEC, 0x11 alternating)
        byte[] pad = [0xEC, 0x11];
        int padIdx = 0;
        while (bits.Count < dataCodewords * 8) {
            AddBits(bits, pad[padIdx++ % 2], 8);
        }

        byte[] dc = new byte[dataCodewords];
        for (int i = 0; i < dataCodewords; i++) {
            int b = 0;
            for (int bit = 0; bit < 8; bit++) {
                if (bits[i * 8 + bit]) b |= (1 << (7 - bit));
            }
            dc[i] = (byte)b;
        }

        // Reed-Solomon Error Correction computation
        byte[] ec = ComputeReedSolomon(dc, ecCodewords);

        // Combine data and EC codewords
        byte[] allCodewords = new byte[totalCodewords];
        Buffer.BlockCopy(dc, 0, allCodewords, 0, dataCodewords);
        Buffer.BlockCopy(ec, 0, allCodewords, dataCodewords, ecCodewords);

        int matrixSize = 17 + version * 4;
        bool[,] matrix = new bool[matrixSize, matrixSize];
        bool[,] reserved = new bool[matrixSize, matrixSize];

        // 1. Finder patterns
        PlaceFinder(matrix, reserved, 0, 0);
        PlaceFinder(matrix, reserved, matrixSize - 7, 0);
        PlaceFinder(matrix, reserved, 0, matrixSize - 7);

        // 2. Alignment patterns (for Version >= 2)
        if (version >= 2) {
            int alignPos = version switch { 2 => 18, 3 => 22, 4 => 26, 5 => 30, 6 => 34, _ => 18 };
            PlaceAlignment(matrix, reserved, alignPos, alignPos);
        }

        // 3. Timing patterns
        for (int i = 8; i < matrixSize - 8; i++) {
            bool val = (i % 2 == 0);
            if (!reserved[6, i]) { matrix[6, i] = val; reserved[6, i] = true; }
            if (!reserved[i, 6]) { matrix[i, 6] = val; reserved[i, 6] = true; }
        }

        // 4. Reserve format info areas
        for (int i = 0; i < 9; i++) {
            reserved[8, i] = true; reserved[i, 8] = true;
            reserved[8, matrixSize - 1 - i] = true; reserved[matrixSize - 1 - i, 8] = true;
        }
        reserved[matrixSize - 8, 8] = true; // Dark module
        matrix[matrixSize - 8, 8] = true;

        // 5. Place data bits using standard serpentine zig-zag
        var allBits = new List<bool>();
        foreach (byte b in allCodewords) {
            for (int bit = 7; bit >= 0; bit--) allBits.Add(((b >> bit) & 1) != 0);
        }

        int bitIndex = 0;
        int dir = -1; // Upward
        int col = matrixSize - 1;

        while (col > 0) {
            if (col == 6) col--; // Skip vertical timing column
            int row = dir < 0 ? matrixSize - 1 : 0;
            while (row >= 0 && row < matrixSize) {
                for (int c = 0; c < 2; c++) {
                    int cc = col - c;
                    if (!reserved[row, cc]) {
                        bool bit = bitIndex < allBits.Count && allBits[bitIndex++];
                        // Apply Mask Pattern 0: (row + col) % 2 == 0
                        if ((row + cc) % 2 == 0) bit = !bit;
                        matrix[row, cc] = bit;
                    }
                }
                row += dir;
            }
            dir = -dir;
            col -= 2;
        }

        // 6. Write format info for EC Level L, Mask 0 (Format bits = 0x77C4 with BCH masking)
        // Level L = 01, Mask 0 = 000 -> 01000b -> with BCH 15 bits is 111011111000100b ^ 101010000010010b = 010001111010110b
        ushort formatBits = 0x77C4;
        WriteFormatInfo(matrix, formatBits, matrixSize);

        return matrix;
    }

    private static void AddBits(List<bool> list, int val, int count) {
        for (int i = count - 1; i >= 0; i--) list.Add(((val >> i) & 1) != 0);
    }

    private static void PlaceFinder(bool[,] m, bool[,] res, int r, int c) {
        for (int y = -1; y <= 7; y++) {
            for (int x = -1; x <= 7; x++) {
                int ry = r + y;
                int rx = c + x;
                if (ry >= 0 && ry < m.GetLength(0) && rx >= 0 && rx < m.GetLength(1)) {
                    bool val = (y >= 0 && y <= 6 && (x == 0 || x == 6 || y == 0 || y == 6)) ||
                               (y >= 2 && y <= 4 && x >= 2 && x <= 4);
                    m[ry, rx] = val;
                    res[ry, rx] = true;
                }
            }
        }
    }

    private static void PlaceAlignment(bool[,] m, bool[,] res, int r, int c) {
        for (int y = -2; y <= 2; y++) {
            for (int x = -2; x <= 2; x++) {
                int ry = r + y;
                int rx = c + x;
                bool val = Math.Abs(y) == 2 || Math.Abs(x) == 2 || (y == 0 && x == 0);
                m[ry, rx] = val;
                res[ry, rx] = true;
            }
        }
    }

    private static void WriteFormatInfo(bool[,] m, ushort format, int size) {
        for (int i = 0; i < 15; i++) {
            bool bit = ((format >> i) & 1) != 0;
            // Top-left
            if (i < 6) m[i, 8] = bit;
            else if (i == 6) m[7, 8] = bit;
            else if (i == 7) m[8, 8] = bit;
            else if (i == 8) m[8, 7] = bit;
            else m[8, 14 - i] = bit;

            // Split across top-right and bottom-left
            if (i < 8) m[8, size - 1 - i] = bit;
            else m[size - 15 + i, 8] = bit;
        }
    }

    private static byte[] ComputeReedSolomon(byte[] data, int ecCount) {
        // Build generator polynomial
        byte[] gen = [1];
        for (int i = 0; i < ecCount; i++) {
            byte[] next = new byte[gen.Length + 1];
            byte factor = Exp[i];
            for (int j = 0; j < gen.Length; j++) {
                next[j] ^= GfMul(gen[j], factor);
                next[j + 1] ^= gen[j];
            }
            gen = next;
        }

        byte[] rem = new byte[ecCount];
        foreach (byte d in data) {
            byte factor = (byte)(d ^ rem[0]);
            for (int j = 0; j < ecCount - 1; j++) {
                rem[j] = (byte)(rem[j + 1] ^ GfMul(gen[ecCount - 1 - j], factor));
            }
            rem[ecCount - 1] = GfMul(gen[0], factor);
        }
        return rem;
    }
}
