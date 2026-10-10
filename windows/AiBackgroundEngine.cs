using System;
using System.IO;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace S8Cam;

/// <summary>
/// Hardware-accelerated AI Portrait Segmentation engine running on DirectML / GPU (RTX 4070 Ti).
/// Uses u2netp (320x320) model with temporal smoothing (EMA) to eliminate edge jitter at 60 FPS.
/// </summary>
public sealed class AiBackgroundEngine : IDisposable {
    private const int ModelDim = 320;
    private const int ModelSize = ModelDim * ModelDim;

    private InferenceSession? session;
    private readonly float[] inputTensorData = new float[1 * 3 * ModelDim * ModelDim];
    private readonly float[] smoothedMask = new float[ModelSize];
    private readonly float[] rawMask = new float[ModelSize];
    private bool hasPreviousMask;
    private readonly object sync = new();
    private bool disposed;

    // Zero-latency decoupled mask cache (up to 4K: 3840x2160)
    private readonly byte[] cachedMask = new byte[3840 * 2160];
    private readonly byte[] workerMask = new byte[3840 * 2160];
    private int cachedMaskWidth = 0;
    private int cachedMaskHeight = 0;
    private bool hasCachedMask = false;
    private readonly object cacheLock = new();

    private volatile int inferenceRunning = 0;
    private long lastInferenceTick = 0;

    // Fast 1024-entry Sigmoid LUT
    private static readonly byte[] SigmoidLut = PrecomputeSigmoidLut(0.15f);

    private static byte[] PrecomputeSigmoidLut(float feather) {
        var lut = new byte[1024];
        float slope = 6.0f / Math.Max(0.05f, feather);
        for (int i = 0; i < 1024; i++) {
            float val = i / 1023.0f;
            float f = (val - 0.5f) * slope;
            float sigmoid = 1.0f / (1.0f + MathF.Exp(-f));
            lut[i] = (byte)Math.Clamp((int)(sigmoid * 255.0f + 0.5f), 0, 255);
        }
        return lut;
    }

    public bool IsAvailable => session != null;
    public bool IsDirectMl { get; private set; }
    public string DeviceDescription { get; private set; } = "Initializing...";

    private static AiBackgroundEngine? instance;
    private static readonly object instanceLock = new();

    public static AiBackgroundEngine Instance {
        get {
            if (instance == null) {
                lock (instanceLock) {
                    instance ??= new AiBackgroundEngine();
                }
            }
            return instance;
        }
    }

    public AiBackgroundEngine() {
        Initialize();
    }

    private void Initialize() {
        var modelPath = FindModelPath();
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) {
            DeviceDescription = "AI Model file not found (u2netp.onnx)";
            return;
        }

        try {
            var options = new SessionOptions {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };

            // Attempt DirectML GPU acceleration first (NVIDIA GeForce RTX 4070 Ti / D3D12)
            try {
                options.AppendExecutionProvider_DML(0);
                IsDirectMl = true;
                DeviceDescription = "DirectML GPU / D3D12";
            } catch (Exception ex) {
                IsDirectMl = false;
                DeviceDescription = "CPU Fallback (" + ex.Message + ")";
            }

            session = new InferenceSession(modelPath, options);
            if (!IsDirectMl) {
                DeviceDescription = "CPU Fallback (Session Active)";
            }
        } catch (Exception ex) {
            session = null;
            DeviceDescription = "AI Engine error: " + ex.Message;
        }
    }

    private static string? FindModelPath() {
        string[] candidates = [
            Path.Combine(AppContext.BaseDirectory, "models", "u2netp.onnx"),
            Path.Combine(AppContext.BaseDirectory, "u2netp.onnx"),
            Path.Combine(Environment.CurrentDirectory, "windows", "models", "u2netp.onnx"),
            Path.Combine(Environment.CurrentDirectory, "models", "u2netp.onnx"),
            Path.Combine(Environment.CurrentDirectory, "u2netp.onnx")
        ];

        foreach (var path in candidates) {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Processes a full-resolution BGRA frame and fills a full-resolution 8-bit alpha mask:
    /// 255 = person (foreground), 0 = background.
    /// Non-blocking: returns cached mask immediately (< 0.2ms) and updates in background at 30-40 FPS.
    /// </summary>
    public unsafe void GenerateMask(byte[] bgraFrame, int width, int height, byte[] outMask, float edgeFeather = 0.15f) {
        if (session == null || bgraFrame == null || outMask == null) return;
        if (width <= 0 || height <= 0 || outMask.Length < width * height) return;

        bool hasValidCache;
        lock (cacheLock) {
            hasValidCache = hasCachedMask && cachedMaskWidth == width && cachedMaskHeight == height;
            if (hasValidCache) {
                Buffer.BlockCopy(cachedMask, 0, outMask, 0, width * height);
            }
        }

        if (!hasValidCache) {
            // First run or resolution change: execute synchronously so caller immediately has a mask
            RunInferenceCore(bgraFrame, null, width, height, outMask, edgeFeather);
            lock (cacheLock) {
                Buffer.BlockCopy(outMask, 0, cachedMask, 0, width * height);
                cachedMaskWidth = width;
                cachedMaskHeight = height;
                hasCachedMask = true;
            }
            return;
        }

        // Trigger decoupled async inference at ~35-40 FPS target (>= 25 ms interval)
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsedMs = (now - lastInferenceTick) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs >= 25 && Interlocked.CompareExchange(ref inferenceRunning, 1, 0) == 0) {
            lastInferenceTick = now;
            PrepareInputTensor(bgraFrame, width, height);
            Task.Run(() => {
                try {
                    RunInferenceFromPreparedTensor(width, height, edgeFeather);
                } finally {
                    Interlocked.Exchange(ref inferenceRunning, 0);
                }
            });
        }
    }

    /// <summary>
    /// Processes a full-resolution NV12 frame and fills a full-resolution 8-bit alpha mask:
    /// 255 = person (foreground), 0 = background.
    /// Non-blocking: returns cached mask immediately (< 0.2ms) and updates in background at 30-40 FPS.
    /// </summary>
    public unsafe void GenerateMaskFromNv12(byte[] nv12Frame, int width, int height, byte[] outMask, float edgeFeather = 0.15f) {
        if (session == null || nv12Frame == null || outMask == null) return;
        if (width <= 0 || height <= 0 || outMask.Length < width * height) return;

        bool hasValidCache;
        lock (cacheLock) {
            hasValidCache = hasCachedMask && cachedMaskWidth == width && cachedMaskHeight == height;
            if (hasValidCache) {
                Buffer.BlockCopy(cachedMask, 0, outMask, 0, width * height);
            }
        }

        if (!hasValidCache) {
            RunInferenceCore(null, nv12Frame, width, height, outMask, edgeFeather);
            lock (cacheLock) {
                Buffer.BlockCopy(outMask, 0, cachedMask, 0, width * height);
                cachedMaskWidth = width;
                cachedMaskHeight = height;
                hasCachedMask = true;
            }
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsedMs = (now - lastInferenceTick) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs >= 25 && Interlocked.CompareExchange(ref inferenceRunning, 1, 0) == 0) {
            lastInferenceTick = now;
            PrepareInputTensorFromNv12(nv12Frame, width, height);
            Task.Run(() => {
                try {
                    RunInferenceFromPreparedTensor(width, height, edgeFeather);
                } finally {
                    Interlocked.Exchange(ref inferenceRunning, 0);
                }
            });
        }
    }

    private void RunInferenceFromPreparedTensor(int width, int height, float edgeFeather) {
        lock (sync) {
            if (disposed || session == null) return;

            var inputTensor = new DenseTensor<float>(inputTensorData, [1, 3, ModelDim, ModelDim]);
            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("input.1", inputTensor)
            };

            using var results = session.Run(inputs);
            var output = results[0].AsTensor<float>();

            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            int i = 0;
            foreach (var val in output) {
                if (i >= ModelSize) break;
                rawMask[i] = val;
                if (val < minVal) minVal = val;
                if (val > maxVal) maxVal = val;
                i++;
            }

            float range = Math.Max(maxVal - minVal, 1e-6f);
            float invRange = 1.0f / range;

            float alpha = hasPreviousMask ? 0.82f : 1.0f;
            float invAlpha = 1.0f - alpha;

            for (int k = 0; k < ModelSize; k++) {
                float norm = (rawMask[k] - minVal) * invRange;
                if (hasPreviousMask) {
                    smoothedMask[k] = (smoothedMask[k] * invAlpha) + (norm * alpha);
                } else {
                    smoothedMask[k] = norm;
                }
            }
            hasPreviousMask = true;

            UpscaleMaskToFullResolution(smoothedMask, workerMask, width, height, edgeFeather);

            lock (cacheLock) {
                Buffer.BlockCopy(workerMask, 0, cachedMask, 0, width * height);
                cachedMaskWidth = width;
                cachedMaskHeight = height;
                hasCachedMask = true;
            }
        }
    }

    private void RunInferenceCore(byte[]? bgra, byte[]? nv12, int width, int height, byte[] outMask, float edgeFeather) {
        lock (sync) {
            if (disposed || session == null) return;
            if (bgra != null) PrepareInputTensor(bgra, width, height);
            else if (nv12 != null) PrepareInputTensorFromNv12(nv12, width, height);

            var inputTensor = new DenseTensor<float>(inputTensorData, [1, 3, ModelDim, ModelDim]);
            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("input.1", inputTensor)
            };

            using var results = session.Run(inputs);
            var output = results[0].AsTensor<float>();

            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            int i = 0;
            foreach (var val in output) {
                if (i >= ModelSize) break;
                rawMask[i] = val;
                if (val < minVal) minVal = val;
                if (val > maxVal) maxVal = val;
                i++;
            }

            float range = Math.Max(maxVal - minVal, 1e-6f);
            float invRange = 1.0f / range;

            float alpha = hasPreviousMask ? 0.82f : 1.0f;
            float invAlpha = 1.0f - alpha;

            for (int k = 0; k < ModelSize; k++) {
                float norm = (rawMask[k] - minVal) * invRange;
                if (hasPreviousMask) {
                    smoothedMask[k] = (smoothedMask[k] * invAlpha) + (norm * alpha);
                } else {
                    smoothedMask[k] = norm;
                }
            }
            hasPreviousMask = true;

            UpscaleMaskToFullResolution(smoothedMask, outMask, width, height, edgeFeather);
        }
    }

    private void PrepareInputTensorFromNv12(byte[] nv12, int width, int height) {
        float stepX = (float)width / ModelDim;
        float stepY = (float)height / ModelDim;

        const float meanR = 0.485f, meanG = 0.456f, meanB = 0.406f;
        const float invStdR = 1.0f / 0.229f, invStdG = 1.0f / 0.224f, invStdB = 1.0f / 0.225f;

        int planeSize = ModelSize;
        int rOffset = 0;
        int gOffset = planeSize;
        int bOffset = planeSize * 2;
        int uvPlaneOffset = width * height;

        Parallel.For(0, ModelDim, dy => {
            int srcY = Math.Clamp((int)(dy * stepY), 0, height - 1);
            int srcYRow = srcY * width;
            int srcUvRow = uvPlaneOffset + (srcY / 2) * width;
            int destRow = dy * ModelDim;

            for (int dx = 0; dx < ModelDim; dx++) {
                int srcX = Math.Clamp((int)(dx * stepX), 0, width - 1);
                byte yVal = nv12[srcYRow + srcX];
                int uvIdx = srcUvRow + (srcX & ~1);
                byte uVal = nv12[uvIdx];
                byte vVal = nv12[uvIdx + 1];

                int c = yVal - 16;
                int d = uVal - 128;
                int e = vVal - 128;
                int r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                int g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                int b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);

                int dstIdx = destRow + dx;
                inputTensorData[rOffset + dstIdx] = ((r / 255.0f) - meanR) * invStdR;
                inputTensorData[gOffset + dstIdx] = ((g / 255.0f) - meanG) * invStdG;
                inputTensorData[bOffset + dstIdx] = ((b / 255.0f) - meanB) * invStdB;
            }
        });
    }

    /// <summary>
    /// Downscales incoming BGRA frame to 320x320 NCHW float tensor.
    /// Mean = [0.485, 0.456, 0.406], Std = [0.229, 0.224, 0.225]
    /// </summary>
    private void PrepareInputTensor(byte[] bgra, int width, int height) {
        float stepX = (float)width / ModelDim;
        float stepY = (float)height / ModelDim;

        const float meanR = 0.485f, meanG = 0.456f, meanB = 0.406f;
        const float invStdR = 1.0f / 0.229f, invStdG = 1.0f / 0.224f, invStdB = 1.0f / 0.225f;

        int planeSize = ModelSize;
        int rOffset = 0;
        int gOffset = planeSize;
        int bOffset = planeSize * 2;

        Parallel.For(0, ModelDim, dy => {
            int srcY = Math.Clamp((int)(dy * stepY), 0, height - 1);
            int srcRow = srcY * width * 4;
            int destRow = dy * ModelDim;

            for (int dx = 0; dx < ModelDim; dx++) {
                int srcX = Math.Clamp((int)(dx * stepX), 0, width - 1);
                int srcIdx = srcRow + (srcX * 4);

                byte b = bgra[srcIdx];
                byte g = bgra[srcIdx + 1];
                byte r = bgra[srcIdx + 2];

                int dstIdx = destRow + dx;
                inputTensorData[rOffset + dstIdx] = ((r / 255.0f) - meanR) * invStdR;
                inputTensorData[gOffset + dstIdx] = ((g / 255.0f) - meanG) * invStdG;
                inputTensorData[bOffset + dstIdx] = ((b / 255.0f) - meanB) * invStdB;
            }
        });
    }

    /// <summary>
    private static int[]? cachedX0Tab;
    private static int[]? cachedX1Tab;
    private static float[]? cachedWxTab;
    private static float[]? cachedInvWxTab;
    private static int cachedTableWidth = 0;
    private static readonly object tableLock = new();

    private static void UpscaleMaskToFullResolution(float[] src320, byte[] dstMask, int width, int height, float feather) {
        var lut = Math.Abs(feather - 0.15f) < 0.01f ? SigmoidLut : PrecomputeSigmoidLut(feather);
        float scaleX = (float)(ModelDim - 1) / Math.Max(1, width - 1);
        float scaleY = (float)(ModelDim - 1) / Math.Max(1, height - 1);

        int[] x0Tab;
        int[] x1Tab;
        float[] wxTab;
        float[] invWxTab;

        lock (tableLock) {
            if (cachedTableWidth != width || cachedX0Tab == null || cachedX1Tab == null || cachedWxTab == null || cachedInvWxTab == null) {
                cachedX0Tab = new int[width];
                cachedX1Tab = new int[width];
                cachedWxTab = new float[width];
                cachedInvWxTab = new float[width];
                for (int x = 0; x < width; x++) {
                    float srcX = x * scaleX;
                    int x0 = (int)srcX;
                    cachedX0Tab[x] = x0;
                    cachedX1Tab[x] = Math.Min(x0 + 1, ModelDim - 1);
                    float wx = srcX - x0;
                    cachedWxTab[x] = wx;
                    cachedInvWxTab[x] = 1.0f - wx;
                }
                cachedTableWidth = width;
            }
            x0Tab = cachedX0Tab!;
            x1Tab = cachedX1Tab!;
            wxTab = cachedWxTab!;
            invWxTab = cachedInvWxTab!;
        }

        Parallel.For(0, height, y => {
            float srcY = y * scaleY;
            int y0 = (int)srcY;
            int y1 = Math.Min(y0 + 1, ModelDim - 1);
            float wy = srcY - y0;
            float invWy = 1.0f - wy;

            int row0 = y0 * ModelDim;
            int row1 = y1 * ModelDim;
            int dstRow = y * width;

            for (int x = 0; x < width; x++) {
                float top = (src320[row0 + x0Tab[x]] * invWxTab[x]) + (src320[row0 + x1Tab[x]] * wxTab[x]);
                float bot = (src320[row1 + x0Tab[x]] * invWxTab[x]) + (src320[row1 + x1Tab[x]] * wxTab[x]);
                float val = (top * invWy) + (bot * wy);

                int lutIdx = Math.Clamp((int)(val * 1023.0f + 0.5f), 0, 1023);
                dstMask[dstRow + x] = lut[lutIdx];
            }
        });
    }

    public void Dispose() {
        lock (sync) {
            if (disposed) return;
            disposed = true;
            session?.Dispose();
            session = null;
        }
    }
}
