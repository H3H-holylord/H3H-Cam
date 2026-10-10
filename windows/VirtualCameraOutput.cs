using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace S8Cam;

public sealed class VirtualCameraOutput : IAsyncDisposable {
    private readonly VirtualCameraWriter? writer;
    private readonly SpoutSender? spout;
    private readonly NamedPipeServerStream? pipeServer;
    private readonly Process? decoder;
    private readonly CancellationTokenSource stop;
    private readonly Task? pump;
    private Settings settings;
    private readonly long startedAt=Environment.TickCount64;
    private long lastPublishedAt=Environment.TickCount64;
    public bool Stalled => decoder!=null && !Volatile.Read(ref settings).PrivacyMute &&
        Environment.TickCount64-startedAt>8000 && Environment.TickCount64-Interlocked.Read(ref lastPublishedAt)>3000;
    public long SpoutFrames => spout?.Frames ?? 0;
    public long GpuDroppedFrames => spout?.GpuDroppedFrames ?? 0;
    public int? DecoderProcessId => decoder?.Id;
    public event Action? FramePublished;
    private long frames;
    private long skippedFrames;
    public long SkippedFrames => Interlocked.Read(ref skippedFrames);

    public bool Alive => decoder == null || (!decoder.HasExited && (pump == null || !pump.IsCompleted));
    public long Frames => writer?.Frames ?? spout?.Frames ?? Interlocked.Read(ref frames);

    public void UpdateSettings(Settings next) => Volatile.Write(ref settings, next.Clone());

    private readonly bool isBgra;
    private volatile bool previewEnabled;
    private readonly LatestFramePreview? previewWorker;
    public bool CanSharePreview => decoder != null && Alive;
    public bool RequiresFormatChange(Settings next) => isBgra != RequiresBgra(next,spout?.GpuNv12Ready==true);
    private static bool RequiresBgra(Settings s,bool nv12GpuReady) {
        bool wb=StudioEffectsProcessor.HasColorAdjustment("none",0,1,1,s.WbRedGain,s.WbGreenGain,s.WbBlueGain);
        bool color=StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile,s.Brightness,s.Contrast,s.Saturation,
            s.WbRedGain,s.WbGreenGain,s.WbBlueGain);
        // Keep the RGB effects/WB/alpha contract when both outputs are enabled.
        // Neutral NV12 and Spout-only GPU grading can avoid full-size RGB copies.
        return (s.VirtualCamera&&wb) || (s.SpoutOutput&&(!nv12GpuReady || s.BackgroundEffect!="none" ||
            s.SkinSmoothing || (s.VirtualCamera&&color)));
    }
    public event Action<byte[], int, int>? PreviewFrame;
    public void SetPreviewEnabled(bool enabled) {
        previewEnabled=enabled;
        previewWorker?.SetEnabled(enabled);
    }


    public VirtualCameraOutput(Settings settings, string sdp, CancellationToken cancellation, Action<string> log) {
        this.settings = settings.Clone();
        var outDim = settings.OutputDimensions;
        var finalDim = settings.FinalOutputDimensions;

        if (settings.SpoutOutput) {
            try {
                spout = new SpoutSender("H3HCam", finalDim.Width, finalDim.Height);
                log($"Spout2: активирован аппаратный D3D11 сендер «H3HCam» ({finalDim.Width}x{finalDim.Height})");
            } catch (Exception ex) {
                log("⚠️ Spout2 ошибка: " + ex.Message);
            }
        }

        isBgra=RequiresBgra(settings,spout?.GpuNv12Ready==true);

        if (settings.VirtualCamera) {
            try {
                writer = new VirtualCameraWriter(finalDim.Width, finalDim.Height, settings.Fps);
                log($"Камера для приложений: выберите «{VirtualCameraDriver.GetStatus().DeviceName}» ({finalDim.Width}x{finalDim.Height}). После установки обновите список камер или перезапустите приложение видеозвонков.");
            } catch (Exception ex) {
                log("⚠️ Виртуальная камера недоступна: " + ex.Message);
            }


        }

        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (writer == null && spout == null) {
            return;
        }

        var pixFmt = isBgra ? "bgra" : "nv12";
        var frameBytes = isBgra ? outDim.Width * outDim.Height * 4 : outDim.Width * outDim.Height * 3 / 2;

        var pipeName = $"h3hcam_video_{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{pipeName}";

        pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            256 * 1024,
            64 * 1024
        );

        try {
            decoder = Processes.Start(settings.FfmpegPath, [
                "-hide_banner", "-loglevel", "warning", "-nostdin",
                // nobuffer discards the IDR consumed during SDP probing, breaking HEVC references.
                "-flags", "low_delay",
                "-analyzeduration", "100000", "-probesize", "100000",
                "-hwaccel", "auto",
                "-protocol_whitelist", "file,udp,rtp",
                "-buffer_size", "16777216", "-max_delay", "30000", "-reorder_queue_size", "256",
                "-i", sdp,
                "-map", "0:v:0", "-an",
                "-vf", settings.BuildVideoFilter(outDim.Width, outDim.Height,nv12Limited601:!isBgra),
                "-pix_fmt", pixFmt,
                "-fps_mode", "passthrough",
                "-f", "rawvideo", "-y", pipePath
            ], line => log("Direct/Virtual Cam · " + line), stdout: false, videoPriority: true);
            var previewDim=PreviewDecoder.PreviewDimensions(settings);
            previewWorker=new LatestFramePreview(outDim.Width,outDim.Height,previewDim.Width,previewDim.Height,
                (frame,w,h)=>{if(previewEnabled&&!Volatile.Read(ref this.settings).PrivacyMute)PreviewFrame?.Invoke(frame,w,h);},log);
            pump = Pump(frameBytes, log);
        } catch {
            stop.Cancel();
            Processes.Kill(decoder);
            previewWorker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            decoder?.Dispose();
            stop.Dispose();
            pipeServer?.Dispose();
            writer?.Dispose();
            spout?.Dispose();
            throw;
        }
    }

    private async Task Pump(int bytes, Action<string> log) {
        byte[]? upscaledBuffer = null;
        byte[]? writerNv12Buffer = null;
        try {
            if (pipeServer != null && decoder != null) {
                var connectTask = pipeServer.WaitForConnectionAsync(stop.Token);
                var exitTask = decoder.WaitForExitAsync(stop.Token);
                var completed = await Task.WhenAny(connectTask, exitTask);
                if (completed == exitTask) {
                    throw new IOException($"FFmpeg завершился до подключения к Named Pipe (код {decoder.ExitCode})");
                }
                await connectTask;
            }

            if (pipeServer == null) return;
            await LatestFramePump.RunAsync(pipeServer, bytes, frame => {
                if (Volatile.Read(ref settings).PrivacyMute) return;

                var s = Volatile.Read(ref settings);
                var outDim = s.OutputDimensions;
                bool published=false;
                // Copy the untouched source into a bounded preview mailbox. Scaling and
                // UI callbacks run separately; they cannot stall OBS/virtual-camera output.
                if (previewEnabled && PreviewFrame != null) previewWorker?.Submit(frame);
                if (!isBgra) {
                    bool hasEffects = s.BackgroundEffect != "none" || s.SkinSmoothing;
                    bool hasColor = StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile, s.Brightness, s.Contrast, s.Saturation);
                    bool cpuColorApplied = false;

                    // Apply CPU studio effects (blur, green screen, skin smoothing)
                    // Or if Virtual Camera is active and needs CPU color adjustments:
                    if (hasEffects || (writer != null && hasColor)) {
                        StudioEffectsProcessor.ApplyNv12Effects(frame, outDim.Width, outDim.Height,
                            s.BackgroundEffect, s.BackgroundBlurStrength, s.SkinSmoothing,
                            focusNormX: 0.5f, focusNormY: 0.45f,
                            s.ColorProfile, s.Brightness, s.Contrast, s.Saturation,
                            customBgPath: s.CustomBackgroundImage, useAi: s.AiSegmentation, aiEdgeFeather: s.AiEdgeFeather);
                        cpuColorApplied = hasColor;
                    }

                    // FFmpeg has already applied rotation, mirror, crop and padding.
                    // Apply orientation only once, identically in preview/Spout/virtual camera.
                    if (spout != null) {
                        bool ok = spout.TryWriteNv12Frame(
                            frame,
                            outDim.Width,
                            outDim.Height,
                            s.SuperResolution4K ? s.SuperResolutionSharpness : 0,
                            0,
                            false,
                            cpuColorApplied ? "none" : s.ColorProfile,
                            cpuColorApplied ? 0.0 : s.Brightness,
                            cpuColorApplied ? 1.0 : s.Contrast,
                            cpuColorApplied ? 1.0 : s.Saturation,
                            (float)s.WbRedGain,
                            (float)s.WbGreenGain,
                            (float)s.WbBlueGain);
                        if (ok) published = true;
                    }

                    // 2. Virtual Camera Driver Output (Direct memory copy or Super Resolution 4K)
                    if (writer != null) {
                        var finalDim = s.FinalOutputDimensions;
                        int neededNv12 = finalDim.Width * finalDim.Height * 3 / 2;
                        if (s.SuperResolution4K && (outDim.Width != finalDim.Width || outDim.Height != finalDim.Height)) {
                            if (writerNv12Buffer == null || writerNv12Buffer.Length != neededNv12) {
                                writerNv12Buffer = new byte[neededNv12];
                            }
                            SuperResolutionEngine.UpscaleNv12(frame, outDim.Width, outDim.Height, writerNv12Buffer, finalDim.Width, finalDim.Height, s.SuperResolutionSharpness);
                            writer.Write(writerNv12Buffer);
                            published = true;
                        } else {
                            writer.Write(frame);
                            published = true;
                        }
                    }
                } else {
                    bool hasEffects = s.BackgroundEffect != "none" || s.SkinSmoothing;
                    bool hasColor = StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile, s.Brightness, s.Contrast, s.Saturation, s.WbRedGain, s.WbGreenGain, s.WbBlueGain);
                    if (hasEffects || hasColor) {
                        StudioEffectsProcessor.ApplyEffects(frame, frame, outDim.Width, outDim.Height,
                            s.BackgroundEffect, s.BackgroundBlurStrength, s.SkinSmoothing,
                            focusNormX: 0.5f, focusNormY: 0.45f,
                            s.ColorProfile, s.Brightness, s.Contrast, s.Saturation,
                            customBgPath: s.CustomBackgroundImage, useAi: s.AiSegmentation, aiEdgeFeather: s.AiEdgeFeather,
                            wbR: s.WbRedGain, wbG: s.WbGreenGain, wbB: s.WbBlueGain);
                    }

                    var finalDim = s.FinalOutputDimensions;
                    int neededNv12 = finalDim.Width * finalDim.Height * 3 / 2;
                    if (s.SuperResolution4K && (outDim.Width != finalDim.Width || outDim.Height != finalDim.Height)) {
                        published=spout?.TryWriteFrame(frame,outDim.Width,outDim.Height,s.SuperResolutionSharpness)==true;
                        if (writer != null) {
                            int neededUpscaled = finalDim.Width * finalDim.Height * 4;
                            if (upscaledBuffer == null || upscaledBuffer.Length != neededUpscaled) {
                                upscaledBuffer = new byte[neededUpscaled];
                            }
                            SuperResolutionEngine.UpscaleBgra(frame, outDim.Width, outDim.Height, upscaledBuffer, finalDim.Width, finalDim.Height, s.SuperResolutionSharpness);
                            if (writerNv12Buffer == null || writerNv12Buffer.Length != neededNv12) {
                                writerNv12Buffer = new byte[neededNv12];
                            }
                            StudioEffectsProcessor.BgraToNv12(upscaledBuffer, writerNv12Buffer, finalDim.Width, finalDim.Height);
                            writer.Write(writerNv12Buffer);
                            published=true;
                        }
                    } else {
                        published=spout?.TryWriteFrame(frame,outDim.Width,outDim.Height)==true;
                        if (writer != null) {
                            if (writerNv12Buffer == null || writerNv12Buffer.Length != neededNv12) {
                                writerNv12Buffer = new byte[neededNv12];
                            }
                            StudioEffectsProcessor.BgraToNv12(frame, writerNv12Buffer, outDim.Width, outDim.Height);
                            writer.Write(writerNv12Buffer);
                            published=true;
                        }
                    }
                }
                if(published) {
                    Interlocked.Exchange(ref lastPublishedAt,Environment.TickCount64);
                    FramePublished?.Invoke();
                }
                Interlocked.Increment(ref frames);
            }, stop.Token, () => Interlocked.Increment(ref skippedFrames), dedicatedVideoThread:true);
        } catch (OperationCanceledException) {}
        catch (Exception ex) { if (!stop.IsCancellationRequested) log("Прямой видеовывод остановлен: " + ex.Message); }
    }

    public async ValueTask DisposeAsync() {
        stop.Cancel();
        if (decoder != null) Processes.Kill(decoder);
        pipeServer?.Dispose();
        try { if (pump != null) await pump; }
        finally {
            if(previewWorker!=null)await previewWorker.DisposeAsync();
            decoder?.Dispose();
            writer?.Dispose();
            spout?.Dispose();
            stop.Dispose();
        }
    }
}
