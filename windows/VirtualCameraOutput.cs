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
    private readonly Task? slatePump;
    private Settings settings;
    private readonly object slateLock = new();
    private byte[]? lastRawFrame;
    private byte[]? cachedLiveBgra;
    private int lastRawW, lastRawH;
    private bool cachedBgraDirty;
    private readonly long startedAt=Environment.TickCount64;
    private long lastPublishedAt=Environment.TickCount64;
    public bool Stalled => decoder!=null && !Volatile.Read(ref settings).PrivacyMute &&
        Environment.TickCount64-startedAt>8000 && Environment.TickCount64-Interlocked.Read(ref lastPublishedAt)>3000;
    public long SpoutFrames => spout?.Frames ?? 0;
    public long GpuDroppedFrames => spout?.GpuDroppedFrames ?? 0;
    public string SpoutChannel => spout?.SenderName ?? "";
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
    public bool RequiresFormatChange(Settings next) =>
        isBgra != RequiresBgra(next, spout?.GpuNv12Ready == true) ||
        (next.SpoutOutput && spout != null && !string.Equals(spout.SenderName, string.IsNullOrWhiteSpace(next.SpoutChannelName) ? "H3HCam" : next.SpoutChannelName.Trim(), StringComparison.Ordinal));
    private static bool RequiresBgra(Settings s,bool nv12GpuReady) {
        bool has3dLut = !string.IsNullOrWhiteSpace(s.LutPath) && s.LutPath != "none" && s.LutStrength > 0.001f;
        bool wb=StudioEffectsProcessor.HasColorAdjustment("none",0,1,1,s.WbRedGain,s.WbGreenGain,s.WbBlueGain);
        bool color=StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile,s.Brightness,s.Contrast,s.Saturation,
            s.WbRedGain,s.WbGreenGain,s.WbBlueGain,s.LutPath,s.LutStrength);
        // Keep the RGB effects/WB/3D-LUT/alpha contract when VirtualCamera needs RGB grading.
        // Neutral NV12 and Spout-only GPU grading (including GPU Texture3D LUT!) avoid full-size RGB copies.
        return (s.VirtualCamera&&(wb||has3dLut)) || (s.SpoutOutput&&(!nv12GpuReady || s.BackgroundEffect!="none" ||
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
                var chName = string.IsNullOrWhiteSpace(settings.SpoutChannelName) ? "H3HCam" : settings.SpoutChannelName.Trim();
                spout = new SpoutSender(chName, finalDim.Width, finalDim.Height);
                log(L.Format("s_9431df0be068", finalDim.Width, finalDim.Height) + $" [{chName}]");
            } catch (Exception ex) {
                log(L.Get("s_67543833b1ed") + ex.Message);
            }
        }

        isBgra=RequiresBgra(settings,spout?.GpuNv12Ready==true);

        if (settings.VirtualCamera) {
            try {
                writer = new VirtualCameraWriter(finalDim.Width, finalDim.Height, settings.Fps);
                log(L.Format("s_d3aa81195c5e", VirtualCameraDriver.GetStatus().DeviceName, finalDim.Width, finalDim.Height));
            } catch (Exception ex) {
                log(L.Get("s_c7473a81c3f9") + ex.Message);
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
            // When constructed while PrivacyMute is off, BuildVideoFilter has no black drawbox;
            // if constructed while PrivacyMute is already on, construct decoder without black drawbox so resume is instant!
            var decodeSettings = settings.Clone();
            decodeSettings.PrivacyMute = false;
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
                "-vf", decodeSettings.BuildVideoFilter(outDim.Width, outDim.Height,nv12Limited601:!isBgra),
                "-pix_fmt", pixFmt,
                "-fps_mode", "passthrough",
                "-f", "rawvideo", "-y", pipePath
            ], line => log("Direct/Virtual Cam · " + line), stdout: false, videoPriority: true);
            var previewDim=PreviewDecoder.PreviewDimensions(settings);
            previewWorker=new LatestFramePreview(outDim.Width,outDim.Height,previewDim.Width,previewDim.Height,
                (frame,w,h)=>{if(previewEnabled&&!Volatile.Read(ref this.settings).PrivacyMute)PreviewFrame?.Invoke(frame,w,h);},log);
            pump = Pump(frameBytes, log);
            slatePump = SlateLoop(stop.Token);
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
                    throw new IOException(L.Format("s_0d3959b61ac5", decoder.ExitCode));
                }
                await connectTask;
            }

            if (pipeServer == null) return;
            await LatestFramePump.RunAsync(pipeServer, bytes, frame => {
                if (Volatile.Read(ref settings).PrivacyMute) return;

                var s = Volatile.Read(ref settings);
                var outDim = s.OutputDimensions;
                bool published=false;

                // Снимок последнего кадра (раз в 12 кадров) для размытой шторки приватности (blurred_freeze)
                if ((Interlocked.Read(ref frames) % 12) == 0) {
                    lock (slateLock) {
                        if (lastRawFrame == null || lastRawFrame.Length != frame.Length) {
                            lastRawFrame = new byte[frame.Length];
                        }
                        Buffer.BlockCopy(frame, 0, lastRawFrame, 0, frame.Length);
                        lastRawW = outDim.Width;
                        lastRawH = outDim.Height;
                        cachedBgraDirty = true;
                    }
                }

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
                            (float)s.WbBlueGain,
                            s.LutPath,
                            s.LutStrength);
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
                    bool hasColor = StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile, s.Brightness, s.Contrast, s.Saturation, s.WbRedGain, s.WbGreenGain, s.WbBlueGain, s.LutPath, s.LutStrength);
                    if (hasEffects || hasColor) {
                        StudioEffectsProcessor.ApplyEffects(frame, frame, outDim.Width, outDim.Height,
                            s.BackgroundEffect, s.BackgroundBlurStrength, s.SkinSmoothing,
                            focusNormX: 0.5f, focusNormY: 0.45f,
                            s.ColorProfile, s.Brightness, s.Contrast, s.Saturation,
                            customBgPath: s.CustomBackgroundImage, useAi: s.AiSegmentation, aiEdgeFeather: s.AiEdgeFeather,
                            wbR: s.WbRedGain, wbG: s.WbGreenGain, wbB: s.WbBlueGain,
                            lutPath: s.LutPath, lutStrength: s.LutStrength);
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
        catch (Exception ex) { if (!stop.IsCancellationRequested) log(L.Get("s_61f56716b8b5") + ex.Message); }
    }

    private async Task SlateLoop(CancellationToken ct) {
        byte[]? slateBgra = null;
        byte[]? slateNv12 = null;
        byte[]? previewSlateBgra = null;
        try {
            while (!ct.IsCancellationRequested) {
                var s = Volatile.Read(ref settings);
                if (!s.PrivacyMute) {
                    await Task.Delay(120, ct);
                    continue;
                }

                var outDim = s.OutputDimensions;
                var finalDim = s.FinalOutputDimensions;
                byte[]? liveBgraForOut = null;

                lock (slateLock) {
                    if (lastRawFrame != null && lastRawW == outDim.Width && lastRawH == outDim.Height) {
                        int neededLiveBgra = outDim.Width * outDim.Height * 4;
                        if (cachedLiveBgra == null || cachedLiveBgra.Length != neededLiveBgra) {
                            cachedLiveBgra = new byte[neededLiveBgra];
                            cachedBgraDirty = true;
                        }
                        if (cachedBgraDirty) {
                            if (isBgra && lastRawFrame.Length >= neededLiveBgra) {
                                Buffer.BlockCopy(lastRawFrame, 0, cachedLiveBgra, 0, neededLiveBgra);
                            } else if (!isBgra && lastRawFrame.Length >= outDim.Width * outDim.Height * 3 / 2) {
                                SpoutSender.Nv12ToBgra(lastRawFrame, cachedLiveBgra, outDim.Width, outDim.Height);
                            }
                            cachedBgraDirty = false;
                        }
                        liveBgraForOut = cachedLiveBgra;
                    }
                }

                bool sent = false;
                if (spout != null) {
                    int neededBgra = outDim.Width * outDim.Height * 4;
                    if (slateBgra == null || slateBgra.Length != neededBgra) slateBgra = new byte[neededBgra];
                    SmartPrivacySlate.RenderBgra(slateBgra, outDim.Width, outDim.Height, s, liveBgraForOut);
                    if (spout.TryWriteFrame(slateBgra, outDim.Width, outDim.Height, 0f)) sent = true;
                }

                if (writer != null) {
                    int neededNv12 = finalDim.Width * finalDim.Height * 3 / 2;
                    if (slateNv12 == null || slateNv12.Length != neededNv12) slateNv12 = new byte[neededNv12];
                    byte[]? liveForFinal = (finalDim.Width == outDim.Width && finalDim.Height == outDim.Height) ? liveBgraForOut : null;
                    SmartPrivacySlate.RenderNv12(slateNv12, finalDim.Width, finalDim.Height, s, liveForFinal);
                    writer.Write(slateNv12);
                    sent = true;
                }

                if (previewEnabled && PreviewFrame != null) {
                    var pDim = PreviewDecoder.PreviewDimensions(s);
                    int pBytes = pDim.Width * pDim.Height * 4;
                    if (previewSlateBgra == null || previewSlateBgra.Length != pBytes) previewSlateBgra = new byte[pBytes];
                    SmartPrivacySlate.RenderBgra(previewSlateBgra, pDim.Width, pDim.Height, s, null);
                    PreviewFrame.Invoke(previewSlateBgra, pDim.Width, pDim.Height);
                }

                if (sent) {
                    Interlocked.Exchange(ref lastPublishedAt, Environment.TickCount64);
                    FramePublished?.Invoke();
                    Interlocked.Increment(ref frames);
                }

                await Task.Delay(125, ct);
            }
        } catch (OperationCanceledException) { }
        catch { }
    }

    public async ValueTask DisposeAsync() {
        stop.Cancel();
        if (decoder != null) Processes.Kill(decoder);
        pipeServer?.Dispose();
        try {
            if (pump != null) await pump;
            if (slatePump != null) await slatePump;
        }
        finally {
            if(previewWorker!=null)await previewWorker.DisposeAsync();
            decoder?.Dispose();
            writer?.Dispose();
            spout?.Dispose();
            stop.Dispose();
        }
    }
}
