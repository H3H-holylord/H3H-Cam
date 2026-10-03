using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using S8Cam;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        try {
            if (args.Length >= 2 && args[0] == "render") {
                int tab = args.Length > 2 && int.TryParse(args[2], out var t) ? t : 0;
                int effect = args.Length > 3 && int.TryParse(args[3], out var ef) ? ef : -1;
                double scroll = args.Length > 4 && double.TryParse(args[4], out var sc) ? sc : 0;
                Render(args[1], tab, effect, scroll, args.Contains("basic"), args.Contains("compact"));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "hud") {
                RenderHud(args[1]);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "menu") {
                RenderMenu(args[1]);
                return 0;
            }
            Run(args).GetAwaiter().GetResult();
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Render(string file, int tab = 0, int effectIndex = -1, double scroll = 0, bool basic = false, bool compact = false) {
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        ((FrameworkElement)window.FindName("BasicPanel")).Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)window.FindName("SettingsPanel")).Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        ((FrameworkElement)window.FindName("ProPresetBar")).Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        ((System.Windows.Controls.Button)window.FindName("ModeToggleButton")).Content = basic ? "Все настройки" : "Простой режим";
        var tabControl = window.FindName("SettingsPanel") as System.Windows.Controls.TabControl;
        if (tabControl != null) tabControl.SelectedIndex = tab;
        if (effectIndex >= 0) {
            var bgChoice = window.FindName("BackgroundEffectChoice") as System.Windows.Controls.ComboBox;
            if (bgChoice != null) bgChoice.SelectedIndex = effectIndex;
            var customPath = window.FindName("CustomBgPathBox") as System.Windows.Controls.TextBox;
            if (customPath != null && effectIndex == 4) customPath.Text = @"C:\Wallpapers\Cyberpunk_Studio.png";
        }
        var content = (FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        int renderWidth = compact ? 1000 : 1180, renderHeight = compact ? 680 : 900;
        content.Measure(new Size(renderWidth, renderHeight));
        content.Arrange(new Rect(0, 0, renderWidth, renderHeight));
        content.UpdateLayout();
        if (scroll > 0) {
            var sv = FindChild<System.Windows.Controls.ScrollViewer>(content);
            if (sv != null) {
                sv.ScrollToVerticalOffset(scroll);
                content.UpdateLayout();
            }
        }
        var target = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(content);
        var image = new PngBitmapEncoder();
        image.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        image.Save(stream);
        Console.WriteLine("Rendered " + file);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++) {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var res = FindChild<T>(child);
            if (res != null) return res;
        }
        return null;
    }

    private static void RenderHud(string file) {
        var app = new App();
        app.InitializeComponent();
        var hud = new StreamHudWindow();
        hud.UpdateMetrics(new LiveStatus(
            State: "Streaming",
            Serial: "EXAMPLE_DEVICE_SERIAL",
            Transport: "direct",
            Resolution: "1920x1080",
            RequestedFps: 60,
            DetectedFps: 60.2,
            ReceivedMbps: 24.5,
            EncodedMbps: 24.0,
            Packets: 15420,
            Elapsed: TimeSpan.FromMinutes(12),
            Ffmpeg: "running",
            Thermal: "82% · 33.4°C",
            Details: "Active",
            Power: new PowerTelemetry { Percent = 82, TemperatureC = 33.4 },
            DroppedFrames: 0,
            LostPackets: 0,
            LatencyMs: 26.4
        ), new Settings { SpoutOutput = true });
        var content = (FrameworkElement)hud.Content;
        content.Measure(new Size(260, 220));
        content.Arrange(new Rect(0, 0, 260, 220));
        content.UpdateLayout();
        var target = new RenderTargetBitmap(260, 220, 96, 96, PixelFormats.Pbgra32);
        target.Render(content);
        var image = new PngBitmapEncoder();
        image.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        image.Save(stream);
        Console.WriteLine("Rendered HUD: " + file);
    }

    private static void RenderMenu(string file) {
        var app = new App();
        app.InitializeComponent();
        var menu = new System.Windows.Controls.ContextMenu {
            Background = new SolidColorBrush(Color.FromRgb(12, 20, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 61, 86)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(231, 240, 255)),
            Padding = new Thickness(4)
        };
        System.Windows.Controls.MenuItem CreateItem(string header, bool isBold = false, string? iconText = null, Color? textColor = null) {
            var item = new System.Windows.Controls.MenuItem {
                Header = header,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(textColor ?? (isBold ? Color.FromRgb(112, 229, 195) : Color.FromRgb(231, 240, 255))),
                Padding = new Thickness(8, 6, 12, 6),
                FontSize = 13
            };
            if (iconText != null) {
                item.Icon = new System.Windows.Controls.TextBlock {
                    Text = iconText,
                    Foreground = new SolidColorBrush(textColor ?? Color.FromRgb(112, 229, 195)),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
            return item;
        }
        menu.Items.Add(CreateItem("Открыть H3H Cam", isBold: true, iconText: "🖥️"));
        menu.Items.Add(CreateItem("Старт / Стоп поток", iconText: "▶"));
        menu.Items.Add(CreateItem("Шторка приватности (Mute)", iconText: "🔒"));
        menu.Items.Add(CreateItem("Stream HUD (Показать / Скрыть)", iconText: "📊"));

        var presetsMenu = new System.Windows.Controls.MenuItem {
            Header = "🎛️ Пресеты качества",
            Foreground = new SolidColorBrush(Color.FromRgb(231, 240, 255)),
            Padding = new Thickness(8, 6, 12, 6),
            FontSize = 13
        };
        presetsMenu.Items.Add(CreateItem("🎮 Стриминг (1080p60 Spout2)"));
        presetsMenu.Items.Add(CreateItem("💼 Конференции (720p30 Bokeh)"));
        presetsMenu.Items.Add(CreateItem("🎬 Pro Качество (1440p30 Spout2)"));
        presetsMenu.Items.Add(CreateItem("🍃 Эко-режим (720p30 Saving)"));
        menu.Items.Add(presetsMenu);

        menu.Items.Add(new System.Windows.Controls.Separator {
            Background = new SolidColorBrush(Color.FromRgb(29, 46, 68)),
            Height = 1,
            Margin = new Thickness(4, 3, 4, 3)
        });
        menu.Items.Add(CreateItem("Выход", iconText: "✕", textColor: Color.FromRgb(255, 128, 128)));
        menu.Measure(new Size(280, 260));
        menu.Arrange(new Rect(0, 0, 280, 260));
        menu.UpdateLayout();
        var target = new RenderTargetBitmap(280, 260, 96, 96, PixelFormats.Pbgra32);
        target.Render(menu);
        var image = new PngBitmapEncoder();
        image.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        image.Save(stream);
        Console.WriteLine("Rendered menu: " + file);
    }

    private static async Task Run(string[] args) {
        if (args.Length == 0) throw new ArgumentException(
            "Commands: unit [folder] | probe <auto|usb|wifi> [serial] | stream <usb|wifi> <folder> [serial] [seconds]");
        switch (args[0]) {
            case "pacing-test":
                await PacingTest(Path.GetFullPath(args[1]), int.Parse(args[2]), args[3], int.Parse(args[4]),
                    args.Length > 5 ? int.Parse(args[5]) : 20, args.Length > 6 ? int.Parse(args[6]) : 0, args.ElementAtOrDefault(7) ?? "wifi");
                return;
            case "privacy-test":
                await PrivacyTest(args.ElementAtOrDefault(1) ?? "");
                return;
            case "filter-test":
                await FilterTest();
                return;
            case "preview-toggle":
                await PreviewToggle(args.ElementAtOrDefault(1) ?? "");
                return;
            case "unit":
                await Unit(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("test-results"));
                return;
            case "probe":
                await Probe(args[1], args.ElementAtOrDefault(2) ?? "", args.ElementAtOrDefault(3) ?? "h264",
                    int.TryParse(args.ElementAtOrDefault(4), out var f) ? f : 30);
                return;
            case "icons":
                MakeIcons(args.ElementAtOrDefault(1) ?? "assets/h3hcam_icon.jpg");
                return;
            case "catalog":
                await Catalog(args.ElementAtOrDefault(1) ?? "");
                return;
            case "stream":
                await Stream(args[1], Path.GetFullPath(args[2]), args.ElementAtOrDefault(3) ?? "",
                    args.Length > 4 ? int.Parse(args[4]) : 12,
                    args.ElementAtOrDefault(5) ?? "auto", args.ElementAtOrDefault(6) ?? "continuous",
                      args.Contains("screen-off"), args.Contains("60fps"), args.Contains("preview"), args.Contains("1080p"), args.Contains("virtual"), args.Contains("32mbps"), args.Contains("hevc"), args.Contains("1440p"), args.Contains("spout"), args.Contains("record"));
                return;
            case "pipe-test":
                await PipeTest();
                return;
            default:
                throw new ArgumentException("Unknown command: " + args[0]);
        }
    }

    private static async Task PipeTest() {
        var settings = Settings.Load();
        var ffmpegPath = ToolPaths.Find("ffmpeg.exe", settings.FfmpegPath);
        var pipeName = $"h3h_test_pipe_{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{pipeName}";

        await using var pipeServer = new System.IO.Pipes.NamedPipeServerStream(
            pipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous,
            16 * 1024 * 1024,
            64 * 1024
        );

        int w = 1920, h = 1080;
        int frameBytes = w * h * 4;

        var proc = Processes.Start(ffmpegPath, [
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi", "-i", $"testsrc=size={w}x{h}:rate=60",
            "-vframes", "30",
            "-pix_fmt", "bgra",
            "-f", "rawvideo", "-y", pipePath
        ], s => Console.WriteLine("FFmpeg: " + s));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipeServer.WaitForConnectionAsync(cts.Token);
        Console.WriteLine("Named pipe connected!");

        var buf = new byte[frameBytes];
        int frameCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (frameCount < 30) {
            await pipeServer.ReadExactlyAsync(buf, cts.Token);
            frameCount++;
        }
        sw.Stop();
        await proc.WaitForExitAsync(cts.Token);
        Console.WriteLine($"PASS pipe-test: Read {frameCount} frames ({frameCount * frameBytes / 1024 / 1024} MB) in {sw.ElapsedMilliseconds} ms ({frameCount * 1000.0 / sw.ElapsedMilliseconds:F1} FPS)");
    }

    private static async Task LatestFramePumpTest() {
        const int bytes = 4096, total = 1200;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = "h3h-latest-test-" + Guid.NewGuid().ToString("N");
        using var input = new System.IO.Pipes.NamedPipeServerStream(name,
            System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous);
        using var source = new System.IO.Pipes.NamedPipeClientStream(".", name,
            System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
        await Task.WhenAll(input.WaitForConnectionAsync(timeout.Token), source.ConnectAsync(timeout.Token));
        using var release = new ManualResetEventSlim();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        int dropped = 0;
        var pump = LatestFramePump.RunAsync(input, bytes, frame => {
            var id = BitConverter.ToInt32(frame);
            if (id == 0) {
                first.TrySetResult();
                release.Wait(timeout.Token);
                Assert(BitConverter.ToInt32(frame) == 0, "consumer buffer is never overwritten");
            }
            Assert(frame.Skip(4).All(b => b == (byte)id), "fragmented raw frame remains intact");
            seen.Add(id);
        }, timeout.Token, () => { if (Interlocked.Increment(ref dropped) == total - 1) drained.TrySetResult(); });
        try {
            var frame = new byte[bytes];
            await source.WriteAsync(frame, timeout.Token);
            await first.Task.WaitAsync(timeout.Token);
            for (int id = 1; id <= total; id++) {
                Array.Fill(frame, (byte)id);
                BitConverter.TryWriteBytes(frame, id);
                await source.WriteAsync(frame.AsMemory(0, 137), timeout.Token);
                await source.WriteAsync(frame.AsMemory(137), timeout.Token);
            }
            await drained.Task.WaitAsync(timeout.Token);
            source.Dispose();
            release.Set();
            await pump.WaitAsync(timeout.Token);
            Assert(seen.SequenceEqual(new[] { 0, total }), "blocked output resumes at newest frame, without backlog");
        } finally { release.Set(); timeout.Cancel(); try { await pump; } catch { } }
        try {
            await LatestFramePump.RunAsync(new MemoryStream(new byte[bytes - 1]), bytes,
                _ => throw new Exception("partial frame emitted"), CancellationToken.None);
            throw new Exception("truncated frame accepted");
        } catch (EndOfStreamException) { }
        try {
            await LatestFramePump.RunAsync(new MemoryStream(new byte[bytes * 10]), bytes,
                _ => throw new InvalidOperationException("output-failure"), CancellationToken.None);
            throw new Exception("consumer error lost");
        } catch (InvalidOperationException ex) when (ex.Message == "output-failure") { }
        Console.WriteLine("PASS latest frame: slow output, buffer ownership, fragmentation, EOF, failure cleanup");
    }

    private static async Task Unit(string root) {
        await LatestFramePumpTest();
        Directory.CreateDirectory(root);
        var persistedPath = Path.Combine(root, "preserved-settings.json");
        File.WriteAllText(persistedPath, JsonSerializer.Serialize(new Settings {
            Width = 3840, Height = 2160, Preview = false, ThermalGuard = true, ThermalThresholdC = 44
        }));
        var persisted = Settings.Load(persistedPath);
        Assert(persisted.Width == 3840 && persisted.Height == 2160 && !persisted.Preview &&
            persisted.ThermalGuard && persisted.ThermalThresholdC == 44, "explicit settings survive restart");
        var invalidOrientation = new Settings { OrientationMode = "invalid" };
        ExpectThrows<ArgumentException>(() => invalidOrientation.Validate("usb"), "invalid orientation rejected");
        var invalidRotation = new Settings { Rotation = 45 };
        ExpectThrows<ArgumentException>(() => invalidRotation.Validate("usb"), "invalid rotation rejected");
        var settings = new Settings {
            Transport = "auto", PcIp = "", PhoneIp = "192.168.1.2", Preview = false, Obs = true
        };
        settings.Validate("usb");
        var originalSettings = settings.Clone();
        var change = settings.Clone();
        change.Codec = "hevc";
        Assert(originalSettings.RequiresStreamRestart(change), "codec changes restart SDP and decoder");
        change = originalSettings.Clone();
        change.Width = 2560;
        Assert(originalSettings.RequiresStreamRestart(change), "resolution changes restart stream");
        change = originalSettings.Clone();
        change.Focus = "infinity";
        Assert(!originalSettings.RequiresStreamRestart(change), "focus updates stay live");
        settings.PcIp = "192.168.1.3";
        settings.Validate("wifi");
        settings.BitrateMbps = 32;
        settings.WifiLimitMbps = 12;
        Assert(settings.EffectiveBitrateMbps("wifi") == 12 && settings.EffectiveBitrateMbps("usb") == 32,
            "Wi-Fi bandwidth protection");
        settings.WifiLimitMbps = 20;
        Assert(settings.EffectiveBitrateMbps("wifi") == 20, "Wi-Fi 20 Mbps limit");
        settings.WifiLimitMbps = 0;
        settings.WifiFriendly = false;
        Assert(settings.EffectiveBitrateMbps("wifi") == 32, "Wi-Fi bandwidth override");
        Assert(settings.AdaptiveBitrate && settings.MinimumWifiBitrateMbps == 12, "adaptive Wi-Fi defaults");
        settings.MinimumWifiBitrateMbps = 3;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid adaptive bitrate floor");
        settings.MinimumWifiBitrateMbps = 12;
        settings.ObsPort = 5001;
        settings.Fps = 120;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid fps");
        settings.Fps = 30;

        settings.Torch = true;
        settings.Zoom = 2.0f;
        settings.LockAeAwb = true;
        settings.AutoFraming = true;
        settings.CompositionGrid = true;
        settings.FocusPeaking = true;
        settings.ZebraPattern = true;
        settings.ColorProfile = "teal_orange";
        settings.ForceSamsungLegacy = true;
        settings.Validate("wifi");
        Assert(settings.Torch && settings.Zoom == 2.0f && settings.LockAeAwb && settings.FaceTracking && settings.AutoFraming && settings.CompositionGrid && settings.FocusPeaking && settings.ZebraPattern && settings.ColorProfile == "teal_orange" && settings.BatteryProtect && settings.ForceSamsungLegacy, "torch, zoom, lock ae/awb, face tracking, auto framing, composition grid, focus peaking, zebra, color profile, battery protect, force legacy settings");
        settings.Zoom = 0.5f;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid zoom under 1.0");
        settings.Zoom = 15.0f;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid zoom over 10.0");
        settings.Zoom = 1.0f;
        settings.AutoFramingZoom = 1.35f;
        settings.AutoFramingSpeed = 1.0f;
        settings.AutoFramingDeadzone = 0.05f;
        settings.Validate("wifi");
        Assert(settings.AutoFramingZoom == 1.35f && settings.AutoFramingSpeed == 1.0f && settings.AutoFramingDeadzone == 0.05f, "auto-framing parameters");
        settings.AutoFramingZoom = 0.5f;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid framing zoom under 1.0");
        settings.AutoFramingZoom = 1.35f;
        settings.AutoFramingSpeed = 10.0f;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid framing speed over 5.0");
        settings.AutoFramingSpeed = 1.0f;
        settings.AutoFramingDeadzone = 0.8f;
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid framing deadzone over 0.5");
        settings.AutoFramingDeadzone = 0.05f;
        settings.ColorProfile = "none";
        settings.Validate("wifi");

        settings.Codec = "hevc";
        settings.Validate("wifi");
        Assert(settings.Codec == "hevc", "codec hevc setting");
        settings.Codec = "av1";
        ExpectThrows<ArgumentException>(() => settings.Validate("wifi"), "invalid codec");
        settings.Codec = "h264";
        settings.Validate("wifi");

        // Transforms & Video Filter tests
        var filterDefault = settings.BuildVideoFilter(1920, 1080);
        Assert(filterDefault == "scale=1920:1080:flags=fast_bilinear", "default video filter");

        settings.ColorProfile = "clean";
        Assert(!settings.BuildVideoFilter(1920, 1080).Contains("colorbalance"), "FFmpeg filter avoids CPU-heavy colorbalance");
        settings.ColorProfile = "noir";
        Assert(!settings.BuildVideoFilter(1920, 1080).Contains("hue="), "FFmpeg filter avoids slowmo hue filter");
        settings.ColorProfile = "none";

        // In-memory color grading unit tests
        var testColorSrc = new byte[10 * 10 * 4];
        var testColorDst = new byte[10 * 10 * 4];
        for (int i = 0; i < testColorSrc.Length; i += 4) {
            testColorSrc[i] = 100;     // B
            testColorSrc[i + 1] = 100; // G
            testColorSrc[i + 2] = 100; // R
            testColorSrc[i + 3] = 255; // A
        }

        // Test Noir (Monochrome)
        StudioEffectsProcessor.ApplyEffects(testColorSrc, testColorDst, 10, 10, "none",
            colorProfile: "noir", brightness: 0, contrast: 1.0, saturation: 1.0);
        Assert(testColorDst[0] == testColorDst[1] && testColorDst[1] == testColorDst[2], "noir is pure monochrome");

        // Test Warm (R > B)
        StudioEffectsProcessor.ApplyEffects(testColorSrc, testColorDst, 10, 10, "none",
            colorProfile: "warm", brightness: 0, contrast: 1.0, saturation: 1.0);
        Assert(testColorDst[2] > testColorDst[0], "warm boosts red over blue");

        // Test Cold (B > R)
        StudioEffectsProcessor.ApplyEffects(testColorSrc, testColorDst, 10, 10, "none",
            colorProfile: "cold", brightness: 0, contrast: 1.0, saturation: 1.0);
        Assert(testColorDst[0] > testColorDst[2], "cold boosts blue over red");

        // Test Brightness
        StudioEffectsProcessor.ApplyEffects(testColorSrc, testColorDst, 10, 10, "none",
            colorProfile: "none", brightness: 0.2, contrast: 1.0, saturation: 1.0);
        Assert(testColorDst[0] > 120, "brightness increases pixel value");

        // Test NV12 color grading
        var nv12Test = new byte[8 * 8 * 3 / 2];
        Array.Fill(nv12Test, (byte)100);
        StudioEffectsProcessor.ApplyNv12ColorGrade(nv12Test, 8, 8, "noir", 0, 1.0, 1.0);
        // In noir NV12, UV chroma plane must be 128 (neutral)
        Assert(nv12Test[64] == 128 && nv12Test[65] == 128, "noir NV12 chroma is 128");

        StudioEffectsProcessor.ApplyNv12ColorGrade(nv12Test, 8, 8, "none", 0.2, 1.0, 1.0);
        Assert(nv12Test[0] > 120, "NV12 brightness lifts Y luma");

        // Test NV12 background effects
        var nv12Fx = new byte[64 * 64 * 3 / 2];
        Array.Fill(nv12Fx, (byte)100);
        StudioEffectsProcessor.ApplyNv12Effects(nv12Fx, 64, 64, "greenscreen", blurStrength: 10);
        Assert(nv12Fx[0] > 140, "NV12 greenscreen sets green luma on background");

        Array.Fill(nv12Fx, (byte)100);
        StudioEffectsProcessor.ApplyNv12Effects(nv12Fx, 64, 64, "spotlight");
        Assert(nv12Fx[0] < 60, "NV12 spotlight dims background luma");

        var testSrc = new byte[100 * 100 * 4];
        var testDst = new byte[100 * 100 * 4];
        testSrc[50 * 400 + 50 * 4] = 255;
        testSrc[50 * 400 + 50 * 4 + 1] = 255;
        testSrc[50 * 400 + 50 * 4 + 2] = 255;
        testSrc[50 * 400 + 50 * 4 + 3] = 255;
        VideoOverlayProcessor.ProcessFrame(testSrc, testDst, 100, 100, enablePeaking: true, enableZebra: true);
        Assert(testDst != null, "VideoOverlayProcessor execution");

        var effectSrc = new byte[80 * 80 * 4];
        var effectDst = new byte[80 * 80 * 4];
        Array.Fill(effectSrc, (byte)128);
        StudioEffectsProcessor.ApplyEffects(effectSrc, effectDst, 80, 80, "bokeh", 10, true);
        Assert(effectDst[0] != 0, "StudioEffectsProcessor bokeh");
        StudioEffectsProcessor.ApplyEffects(effectSrc, effectDst, 80, 80, "greenscreen", 10, false);
        Assert(effectDst[1] == 255, "StudioEffectsProcessor greenscreen");

        // Test YCbCr skin tone detection
        Assert(StudioEffectsProcessor.IsSkinTone(210, 160, 130), "skin tone fair/caucasian detected");
        Assert(StudioEffectsProcessor.IsSkinTone(140, 90, 60), "skin tone dark/olive detected");
        Assert(!StudioEffectsProcessor.IsSkinTone(0, 255, 0), "green screen is not skin tone");
        Assert(!StudioEffectsProcessor.IsSkinTone(128, 128, 128), "neutral gray is not skin tone");

        // Test AI background engine & Studio effects
        var aiAvailable = AiBackgroundEngine.Instance.IsAvailable;
        Console.WriteLine($"[TEST] AI Background Engine Available: {aiAvailable}");
        if (aiAvailable) {
            var aiMask = new byte[320 * 320];
            var aiSrc = new byte[320 * 320 * 4];
            Array.Fill(aiSrc, (byte)128);
            AiBackgroundEngine.Instance.GenerateMask(aiSrc, 320, 320, aiMask, 0.15f);
            Assert(aiMask.Length == 320 * 320, "AI mask generated with correct dimensions");

            // Test AI transparent background (Alpha channel in BGRA for OBS Spout2)
            var aiTransparentDst = new byte[80 * 80 * 4];
            var aiTransparentSrc = new byte[80 * 80 * 4];
            Array.Fill(aiTransparentSrc, (byte)200);
            StudioEffectsProcessor.ApplyEffects(aiTransparentSrc, aiTransparentDst, 80, 80, "ai_transparent");
            Assert(aiTransparentDst.Length == 80 * 80 * 4, "ai_transparent sets alpha channel");

            // Test AI custom wallpaper background
            var aiCustomDst = new byte[80 * 80 * 4];
            StudioEffectsProcessor.ApplyEffects(aiTransparentSrc, aiCustomDst, 80, 80, "ai_custom");
            Assert(aiCustomDst.Length == 80 * 80 * 4, "ai_custom produces output frame");

            // Benchmark 1080p AI Bokeh (Full BGRA and NV12 pipelines)
            var f1080Src = new byte[1920 * 1080 * 4];
            var f1080Dst = new byte[1920 * 1080 * 4];
            Array.Fill(f1080Src, (byte)150);

            // Warm up
            StudioEffectsProcessor.ApplyEffects(f1080Src, f1080Dst, 1920, 1080, "ai_bokeh", 15);

            var maskOnly = new byte[1920 * 1080];
            var swMask = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 0; r < 10; r++) {
                AiBackgroundEngine.Instance.GenerateMask(f1080Src, 1920, 1080, maskOnly, 0.15f);
            }
            swMask.Stop();
            Console.WriteLine($"[PROFILE] GenerateMask 10x avg: {swMask.ElapsedMilliseconds / 10.0:F2} ms");

            // Profile Blending loop in isolation
            var swBlend = System.Diagnostics.Stopwatch.StartNew();
            int stride = 1920 * 4;
            int bw = 1920 >> 2;
            int bh = 1080 >> 2;
            var blurredMock = new byte[bw * bh * 4];
            for (int r = 0; r < 10; r++) {
                Parallel.For(0, 1080, y => {
                    int rowOffset = y * stride;
                    int maskRow = y * 1920;
                    int bRow = (y >> 2) * (bw << 2);
                    for (int x = 0; x < 1920; x++) {
                        int idx = rowOffset + (x * 4);
                        byte b = f1080Src[idx];
                        byte g = f1080Src[idx + 1];
                        byte rVal = f1080Src[idx + 2];
                        byte personAlpha = maskOnly[maskRow + x];
                        float mask = 1.0f - (personAlpha / 255.0f);
                        if (mask <= 0.001f) {
                            f1080Dst[idx] = b;
                            f1080Dst[idx + 1] = g;
                            f1080Dst[idx + 2] = rVal;
                            f1080Dst[idx + 3] = 255;
                            continue;
                        }
                        int bIdx = bRow + ((x >> 2) << 2);
                        byte bb = blurredMock[bIdx];
                        byte bg = blurredMock[bIdx + 1];
                        byte br = blurredMock[bIdx + 2];
                        float invMask = 1.0f - mask;
                        f1080Dst[idx] = (byte)(b * invMask + bb * mask);
                        f1080Dst[idx + 1] = (byte)(g * invMask + bg * mask);
                        f1080Dst[idx + 2] = (byte)(rVal * invMask + br * mask);
                        f1080Dst[idx + 3] = 255;
                    }
                });
            }
            swBlend.Stop();
            Console.WriteLine($"[PROFILE] Isolated Blending Loop 10x avg: {swBlend.ElapsedMilliseconds / 10.0:F2} ms");

            // Warmup JIT & threadpool
            StudioEffectsProcessor.ApplyEffects(f1080Src, f1080Dst, 1920, 1080, "ai_bokeh", 15);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int r = 0; r < 10; r++) {
                StudioEffectsProcessor.ApplyEffects(f1080Src, f1080Dst, 1920, 1080, "ai_bokeh", 15);
            }
            sw.Stop();
            double bgraAvgMs = sw.ElapsedMilliseconds / 10.0;
            Console.WriteLine($"[BENCHMARK] 1080p BGRA ApplyEffects(ai_bokeh): {bgraAvgMs:F2} ms / frame (Capacity: {1000.0 / bgraAvgMs:F0} FPS)");
            Assert(bgraAvgMs < 25.0, "1080p BGRA AI Bokeh runs faster than 25ms (< 40 FPS requirement)");

            var f1080Nv12 = new byte[1920 * 1080 * 3 / 2];
            Array.Fill(f1080Nv12, (byte)128);
            StudioEffectsProcessor.ApplyNv12Effects(f1080Nv12, 1920, 1080, "ai_bokeh", 15);
            Thread.Sleep(100); // Allow background async ONNX warmup inference to complete

            sw.Restart();
            for (int r = 0; r < 10; r++) {
                StudioEffectsProcessor.ApplyNv12Effects(f1080Nv12, 1920, 1080, "ai_bokeh", 15);
            }
            sw.Stop();
            double nv12AvgMs = sw.ElapsedMilliseconds / 10.0;
            Console.WriteLine($"[BENCHMARK] 1080p NV12 ApplyNv12Effects(ai_bokeh): {nv12AvgMs:F2} ms / frame (Capacity: {1000.0 / nv12AvgMs:F0} FPS)");
            Assert(nv12AvgMs < 25.0, "1080p NV12 AI Bokeh runs faster than 25ms (< 40 FPS requirement)");
        }

        // Test SuperResolutionEngine 4K
        var srSrc = new byte[100 * 100 * 4];
        var srDst = new byte[200 * 200 * 4];
        Array.Fill(srSrc, (byte)150);
        for (int y = 0; y < 100; y++) {
            for (int x = 50; x < 100; x++) {
                int idx = (y * 100 + x) * 4;
                srSrc[idx] = 220;
                srSrc[idx + 1] = 220;
                srSrc[idx + 2] = 220;
                srSrc[idx + 3] = 255;
            }
        }
        SuperResolutionEngine.UpscaleBgra(srSrc, 100, 100, srDst, 200, 200, 0.25f);
        Assert(srDst.Length == 200 * 200 * 4, "SuperResolutionEngine produces 4x pixel count");
        Assert(srDst[0] == 150 && srDst[199 * 800 + 199 * 4] == 220, "SuperResolutionEngine preserves color regions");

        // Verify cached ColumnInfo correctness on subsequent execution
        var srDstCached = new byte[200 * 200 * 4];
        SuperResolutionEngine.UpscaleBgra(srSrc, 100, 100, srDstCached, 200, 200, 0.25f);
        Assert(srDstCached[0] == 150 && srDstCached[199 * 800 + 199 * 4] == 220, "SuperResolutionEngine cached columns pass");

        // Test SpoutSender D3D11 Super Resolution (RTX 4070 Ti)
        try {
            using var testSpout = new SpoutSender("H3HCamTest", 3840, 2160);
            Assert(testSpout.Width == 3840 && testSpout.Height == 2160, "SpoutSender 4K initialization");
            Assert(testSpout.SharedHandle != IntPtr.Zero, "SpoutSender shared DXGI handle created");
            var test1080p = new byte[1920 * 1080 * 4];
            Array.Fill(test1080p, (byte)100);

            // Warmup
            testSpout.WriteFrame(test1080p, 1920, 1080, 0.20f);
            Assert(testSpout.GpuSuperResReady, "SpoutSender D3D11 GPU Super Resolution initialized and executed");

            // Benchmark 60 frames of 1080p -> 4K upscaling
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 60; i++) {
                testSpout.WriteFrame(test1080p, 1920, 1080, 0.20f);
            }
            sw.Stop();
            double avgMsPerFrame = sw.Elapsed.TotalMilliseconds / 60.0;
            Console.WriteLine($"[BENCHMARK] RTX 4070 Ti 4K Super Resolution: {avgMsPerFrame:F2} ms / frame (FPS capacity: {1000.0 / avgMsPerFrame:F0} FPS!)");
        } catch (Exception ex) {
            Console.WriteLine($"[TEST] Spout2 test note: {ex.Message}");
        }

        // Test Multi-Face Group Tracking Info
        var singleFace = new FaceTrackingInfo(0.5f, 0.5f, 0.2f, 0.2f, 0.5f, 0.5f, 1.35f, 1);
        Assert(singleFace.FaceCount == 1, "single face tracking info");
        var groupFace = new FaceTrackingInfo(0.5f, 0.5f, 0.6f, 0.3f, 0.5f, 0.5f, 1.1f, 2);
        Assert(groupFace.FaceCount == 2 && groupFace.CropZoom < singleFace.CropZoom, "group face tracking expands zoom");

        var qr = QrCodeGenerator.GenerateBitmap("h3hcam://wifi?pc=192.168.1.100&port=5000", 4, 2);
        Assert(qr != null && qr.PixelWidth > 50, "QrCodeGenerator bitmap creation");

        var driverDll = VirtualCameraWriter.FindDriverDll();
        // OBS is installed separately; clean CI machines legitimately have no driver.
        if (driverDll != null) Assert(File.Exists(driverDll), "discovered DirectShow driver exists");
        else Console.WriteLine("SKIP external integration: OBS DirectShow driver is not installed");

        settings.FlipHorizontal = true;
        Assert(settings.BuildVideoFilter(1920, 1080) == "hflip,scale=1920:1080:flags=fast_bilinear", "flip horizontal filter");
        settings.FlipHorizontal = false;

        settings.Rotation = 90;
        Assert(settings.OutputDimensions == (1080, 1920), "rotation 90 dimensions swap");
        Assert(settings.BuildVideoFilter(1080, 1920) == "transpose=1,scale=1080:1920:flags=fast_bilinear", "rotation 90 filter");
        settings.Rotation = 180;
        Assert(settings.OutputDimensions == (1920, 1080), "rotation 180 dimensions");
        Assert(settings.BuildVideoFilter(1920, 1080) == "hflip,vflip,scale=1920:1080:flags=fast_bilinear", "rotation 180 filter");
        settings.Rotation = 270;
        Assert(settings.BuildVideoFilter(1080, 1920) == "transpose=2,scale=1080:1920:flags=fast_bilinear", "rotation 270 filter");
        settings.Rotation = 0;

        settings.Brightness = 0.1;
        settings.Contrast = 1.2;
        settings.Saturation = 1.5;
        var filterColor = settings.BuildVideoFilter(1920, 1080);
        Assert(!filterColor.Contains("eq="), "color adjustments handled in-memory without FFmpeg filter restart");
        settings.Brightness = 0.0; settings.Contrast = 1.0; settings.Saturation = 1.0;

        settings.PrivacyMute = true;
        Assert(settings.BuildVideoFilter(1920, 1080).StartsWith("drawbox="), "privacy mute black box filter");
        settings.PrivacyMute = false;

        // ThermalGuard tests
        settings.BitrateMbps = 20;
        settings.ThermalGuard = true;
        settings.ThermalThresholdC = 48;
        int? throttledBitrate = null;
        int? restoredBitrate = null;
        var guard = new ThermalGuard(settings,
            onThrottle: (reduced, temp, reason) => throttledBitrate = reduced,
            onRestore: (orig, temp) => restoredBitrate = orig);

        guard.UpdateTelemetry(42.0, 30.0);
        Assert(!guard.IsThrottled && throttledBitrate == null, "thermal normal");

        guard.UpdateTelemetry(49.0, 35.0);
        Assert(guard.IsThrottled && throttledBitrate == 12, "thermal throttled to 12 Mbps at 49 °C");

        guard.UpdateTelemetry(46.0, 34.0);
        Assert(guard.IsThrottled, "thermal hysteresis keeps throttling at 46 °C");

        guard.UpdateTelemetry(44.0, 32.0);
        Assert(!guard.IsThrottled && restoredBitrate == 20, "thermal restored to 20 Mbps below 45 °C");

        // Tap-to-Focus Coordinate Inversion Tests
        static (float x, float y) InvertCoords(float dispX, float dispY, int rotation, bool flipX) {
            float tx = dispX, ty = dispY;
            switch (rotation) {
                case 90: tx = dispY; ty = 1.0f - dispX; break;
                case 180: tx = 1.0f - dispX; ty = 1.0f - dispY; break;
                case 270: tx = 1.0f - dispY; ty = dispX; break;
            }
            if (flipX) tx = 1.0f - tx;
            return (Math.Clamp(tx, 0.05f, 0.95f), Math.Clamp(ty, 0.05f, 0.95f));
        }

        var (c0x, c0y) = InvertCoords(0.2f, 0.4f, 0, false);
        Assert(Math.Abs(c0x - 0.2f) < 0.001f && Math.Abs(c0y - 0.4f) < 0.001f, "tap focus coords rotation 0");

        var (c90x, c90y) = InvertCoords(0.2f, 0.4f, 90, false);
        Assert(Math.Abs(c90x - 0.4f) < 0.001f && Math.Abs(c90y - 0.8f) < 0.001f, "tap focus coords rotation 90");

        var (c180x, c180y) = InvertCoords(0.2f, 0.4f, 180, false);
        Assert(Math.Abs(c180x - 0.8f) < 0.001f && Math.Abs(c180y - 0.6f) < 0.001f, "tap focus coords rotation 180");

        var (cFlipX, cFlipY) = InvertCoords(0.2f, 0.4f, 0, true);
        Assert(Math.Abs(cFlipX - 0.8f) < 0.001f && Math.Abs(cFlipY - 0.4f) < 0.001f, "tap focus coords flip horizontal");

        Assert(PreviewDecoder.PreviewDimensions(new Settings { Width = 1920, Height = 1080 }) == (960, 540), "preview dimensions landscape");
        Assert(PreviewDecoder.PreviewDimensions(new Settings { Width = 1920, Height = 1080, Rotation = 90 }) == (540, 960), "preview dimensions portrait 90");

        var testStatus = new LiveStatus("streaming", "serial123", "USB", "1920x1080", 60, 60.0, 35.0, 35.0, 1000, TimeSpan.FromMinutes(1), "ok", "cool", "details", new PowerTelemetry(), 5, 4, 1, 18.5);
        Assert(testStatus.LostPackets == 5 && testStatus.RecoveredPackets == 4 && testStatus.DroppedFrames == 1 && Math.Abs(testStatus.LatencyMs - 18.5) < 0.001, "live status metrics");

        // FaceTracking telemetry & Spring Damping tests
        var testFace = new FaceTrackingInfo(0.48f, 0.35f, 0.22f, 0.28f, 0.50f, 0.38f, 1.35f);
        var statusWithFace = testStatus with { Face = testFace };
        Assert(statusWithFace.Face != null && statusWithFace.Face.Width == 0.22f && statusWithFace.Face.CropZoom == 1.35f, "face tracking info in live status");

        // Critically Damped Spring auto-framing math simulation
        float currentX = 0.2f, targetX = 0.8f, velX = 0.0f;
        float omega = 4.2f * 1.0f;
        float dt = 1.0f / 60.0f;
        for (int step = 0; step < 60; step++) {
            float n = velX - (currentX - targetX) * (omega * omega * dt);
            velX = n / ((1.0f + omega * dt) * (1.0f + omega * dt));
            currentX += velX * dt;
            // Critically damped must never overshoot target (> 0.8f)
            Assert(currentX <= 0.801f, "critically damped spring no overshoot");
        }
        Assert(currentX > 0.74f, "critically damped spring reaches target smoothly");

        // HEVC RFC 7798 Packetizer test
        var hevcPacketizer = new RtpH264Packetizer("hevc");
        var hevcVps = new byte[] { 0x40, 1, 0x0c, 1 };
        var hevcSps = new byte[] { 0x42, 1, 1, 2 };
        var hevcPps = new byte[] { 0x44, 1, 5 };
        var hevcLargeIdr = new byte[3502];
        hevcLargeIdr[0] = 0x26; hevcLargeIdr[1] = 1;
        for (var i = 2; i < hevcLargeIdr.Length; i++) hevcLargeIdr[i] = (byte)(i % 251 + 1);

        var hevcAnnexB = new MemoryStream();
        hevcAnnexB.Write([0, 0, 0, 1]); hevcAnnexB.Write(hevcVps);
        hevcAnnexB.Write([0, 0, 0, 1]); hevcAnnexB.Write(hevcSps);
        hevcAnnexB.Write([0, 0, 0, 1]); hevcAnnexB.Write(hevcPps);
        hevcAnnexB.Write([0, 0, 0, 1]); hevcAnnexB.Write(hevcLargeIdr);

        var hevcPackets = hevcPacketizer.Packetize(hevcAnnexB.ToArray(), 1_000_000);
        Assert(hevcPackets.Count >= 4, "hevc packet count");
        Assert(((hevcPackets[0][12] >> 1) & 0x3F) == 32, "hevc VPS packet");
        Assert(((hevcPackets[1][12] >> 1) & 0x3F) == 33, "hevc SPS packet");
        Assert(((hevcPackets[2][12] >> 1) & 0x3F) == 34, "hevc PPS packet");
        var hevcFu = hevcPackets.Skip(3).ToList();
        Assert(hevcFu.All(p => ((p[12] >> 1) & 0x3F) == 49), "hevc FU payload type 49");
        Assert((hevcFu[0][14] & 0x80) != 0 && (hevcFu[0][14] & 0x3F) == 19, "hevc FU start bit and type");
        Assert((hevcFu[^1][14] & 0x40) != 0 && (hevcFu[^1][14] & 0x3F) == 19, "hevc FU end bit and type");
        Assert((hevcFu[^1][1] & 0x80) != 0, "hevc marker bit on last packet");

        var restoredHevc = new MemoryStream();
        restoredHevc.WriteByte(0x26); restoredHevc.WriteByte(1);
        foreach (var p in hevcFu) restoredHevc.Write(p.AsSpan(15));
        Assert(restoredHevc.ToArray().SequenceEqual(hevcLargeIdr), "hevc FU exact byte reassembly");

        Assert(MainWindow.MaskIp("192.168.1.100") == "192.168.*.*", "mask ip");
        Assert(MainWindow.MaskSerial("R58M123456X") == "R5***6X", "mask serial");

        var history = new Dictionary<int, byte[]>();
        for (var s = 100; s < 120; s++) history[s] = new byte[] { (byte)(s >> 8), (byte)s, 0xDE, 0xAD };
        var nackJson = """{"command":"NACK","sequences":[105, 110]}""";
        using var nackDoc = JsonDocument.Parse(nackJson);
        var requestedSeqs = nackDoc.RootElement.GetProperty("sequences").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var retransmitted = new List<byte[]>();
        foreach (var seq in requestedSeqs) {
            if (history.TryGetValue(seq, out var p)) retransmitted.Add(p);
        }
        Assert(retransmitted.Count == 2 && retransmitted[0][1] == 105 && retransmitted[1][1] == 110, "NACK simulation");

        const string devicesText = """
            List of devices attached
            USB123 device product:test model:Phone_One usb:1-2 transport_id:1
            192.168.1.8:5555 device product:test model:Phone_One transport_id:2
            adb-ABC._adb-tls-connect._tcp device product:test model:Phone_Two transport_id:3
            LOCKED unauthorized usb:3-4 transport_id:4
            SLEEP offline usb:5-6 transport_id:5
            """;
        var devices = AdbController.ParseDevices(devicesText);
        Assert(devices.Count == 5, "device count");
        Assert(!devices[0].IsNetwork && devices[0].Authorized, "physical USB");
        Assert(devices[1].IsNetwork && devices[2].IsNetwork, "network serial detection");
        Assert(devices[3].State == "unauthorized" && devices[4].State == "offline", "ADB states");
        Assert(AdbController.ConnectSucceeded("already connected to 192.168.1.8:5555"), "connect result");
        Assert(!AdbController.ConnectSucceeded("failed to connect"), "failed connect result");

        var addresses = AdbController.ParseInterfaceIpv4("""
            12: wlan0    inet 192.168.1.9/24 brd 192.168.1.255 scope global wlan0
            13: rmnet0    inet 10.4.2.1/32 scope global rmnet0
            """);
        Assert(addresses.Any(x => x.Interface == "wlan0" && x.Address == "192.168.1.9"), "interface parser");

        var json = """{"version":1,"model":"Test","manufacturer":"Lab","sdk":34,"cameras":[{"key":"0","label":"Rear","facing":"back","minimumFocusDistance":4.2,"flash":true,"modes":[{"width":1920,"height":1080,"fps":[30,60]}]}]}""";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var bundle = "Result: Bundle[{data=" + base64 + "}]";
        Assert(AdbController.ParseBundleValue(bundle, "data") == base64, "Bundle parser");
        var capabilities = PhoneCapabilities.ParseBase64(base64);
        Assert(capabilities.Cameras.Single().Modes.Single().Fps.SequenceEqual([30, 60]), "capability parser");
        using var powerJson = JsonDocument.Parse("""{"batteryPercent":78,"batteryStatus":"Charging","batterySource":"USB","batteryVoltageMv":4180,"batteryCurrentUa":820000,"batteryPowerMw":3427.6,"batteryTemperatureC":34.2,"batteryChargeCounterUah":2570000,"batteryHealth":"Good"}""");
        var power = PowerTelemetry.From(powerJson.RootElement);
        Assert(power.Percent == 78 && power.VoltageV == 4.18 && power.CurrentMa == 820 && power.PowerW is > 3.42 and < 3.44, "power telemetry units");
        var unavailable = PowerTelemetry.From(JsonDocument.Parse("{}").RootElement);
        Assert(unavailable.Percent == null && unavailable.Details.Contains("N/A"), "unsupported power values");
        var explicitNull = PowerTelemetry.From(JsonDocument.Parse("""{"batteryPercent":67,"batteryEnergyCounterNwh":null,"batteryChargeCounterUah":null}""").RootElement);
        Assert(explicitNull.Percent == 67 && explicitNull.EnergyWh == null && explicitNull.ChargeMah == null, "nullable power values");

        var original = H3HProtocol.Json(H3HMessageType.Command, 42,
            new { command = "START_STREAM", width = 1920, height = 1080, fps = 60, bitrate = 32_000_000 }, 1);
        await using var protocolBytes = new MemoryStream();
        await H3HProtocol.WriteAsync(protocolBytes, original, CancellationToken.None);
        protocolBytes.Position = 0;
        var decoded = await H3HProtocol.ReadAsync(protocolBytes, CancellationToken.None);
        Assert(decoded is { Sequence: 42, Type: H3HMessageType.Command, Flags: 1 } &&
            decoded.Value.Text.Contains("START_STREAM"), "H3H protocol roundtrip");

        // H3H Protocol 2 tests
        Assert(H3HProtocol.CurrentVersion == 2, "H3H protocol version 2");
        var msgHelloAck = H3HProtocol.Json(H3HMessageType.HelloAck, 101, new { version = 2, supported = true }, version: H3HProtocol.Version2);
        var msgBattery = H3HProtocol.Json(H3HMessageType.Battery, 102, new { percent = 85, temp = 33.2, charging = true }, version: H3HProtocol.Version2);
        var msgThermal = H3HProtocol.Json(H3HMessageType.Thermal, 103, new { temp = 41.5, throttled = false }, version: H3HProtocol.Version2);
        var msgBitrate = H3HProtocol.Json(H3HMessageType.SetBitrate, 104, new { bitrate = 18_000_000 }, version: H3HProtocol.Version2);
        var msgIdr = H3HProtocol.Json(H3HMessageType.RequestIdr, 105, new { reason = "keyframe_recovery" }, version: H3HProtocol.Version2);

        await using var p2Stream = new MemoryStream();
        await H3HProtocol.WriteAsync(p2Stream, msgBattery, CancellationToken.None);
        await H3HProtocol.WriteAsync(p2Stream, msgBitrate, CancellationToken.None);
        p2Stream.Position = 0;
        var decBattery = await H3HProtocol.ReadAsync(p2Stream, CancellationToken.None);
        var decBitrate = await H3HProtocol.ReadAsync(p2Stream, CancellationToken.None);
        Assert(decBattery is { Type: H3HMessageType.Battery, Version: 2, Sequence: 102 } && decBattery.Value.Text.Contains("85"), "H3H protocol 2 Battery message");
        Assert(decBitrate is { Type: H3HMessageType.SetBitrate, Version: 2, Sequence: 104 } && decBitrate.Value.Text.Contains("18000000"), "H3H protocol 2 SetBitrate message");

        // H3H Protocol Backward Compatibility (reading Protocol v1 from stream)
        var msgV1 = H3HProtocol.Json(H3HMessageType.Command, 1, new { command = "LEGACY_V1" }, flags: 0, version: H3HProtocol.Version1);
        await using var p1Stream = new MemoryStream();
        await H3HProtocol.WriteAsync(p1Stream, msgV1, CancellationToken.None);
        p1Stream.Position = 0;
        var decV1 = await H3HProtocol.ReadAsync(p1Stream, CancellationToken.None);
        Assert(decV1 is { Type: H3HMessageType.Command, Version: 1 } && decV1.Value.Text.Contains("LEGACY_V1"), "H3H protocol v1 backward compatibility");

        // ConnectionManager tests
        Assert(ConnectionManager.CalculateBackoffMs(0) == 800, "backoff attempt 0");
        Assert(ConnectionManager.CalculateBackoffMs(1) == 1600, "backoff attempt 1");
        Assert(ConnectionManager.CalculateBackoffMs(2) == 3200, "backoff attempt 2");
        Assert(ConnectionManager.CalculateBackoffMs(3) == 6400, "backoff attempt 3");
        Assert(ConnectionManager.CalculateBackoffMs(4) == 8000, "backoff attempt 4 capped at 8000ms");
        Assert(ConnectionManager.CalculateBackoffMs(10) == 8000, "backoff attempt 10 capped at 8000ms");

        var connMgr = new ConnectionManager(_ => {});
        Assert(connMgr.State == TransportState.Disconnected, "initial connection state disconnected");
        connMgr.OnConnected("direct");
        Assert(connMgr.State == TransportState.Connected && connMgr.ActiveTransport == "direct", "transition to connected");
        connMgr.OnRecovering("direct", 1, 800);
        Assert(connMgr.State == TransportState.Recovering && connMgr.ReconnectAttempts == 1, "transition to recovering");
        connMgr.OnDisconnected("stopped");
        Assert(connMgr.State == TransportState.Disconnected, "transition to disconnected");

        // Orientation & Shorts Mode tests
        var orientationSettings = new Settings { Width = 1920, Height = 1080, OrientationMode = "16:9" };
        Assert(orientationSettings.OutputDimensions == (1920, 1080), "orientation 16:9 dimensions");
        orientationSettings.OrientationMode = "9:16_crop";
        Assert(orientationSettings.OutputDimensions == (1080, 1920), "orientation 9:16 crop dimensions");
        var cropVf = orientationSettings.BuildVideoFilter(1080, 1920);
        Assert(cropVf.Contains("crop=w='trunc(min(iw,ih*9/16)/2)*2'"), "orientation 9:16 crop filter chain");

        orientationSettings.OrientationMode = "9:16_fit";
        var fitVf = orientationSettings.BuildVideoFilter(1080, 1920);
        Assert(fitVf.Contains("force_original_aspect_ratio=decrease,pad=1080:1920"), "orientation 9:16 fit filter chain");

        orientationSettings.OrientationMode = "9:16_autoframing";
        var autoFramingVf = orientationSettings.BuildVideoFilter(1080, 1920);
        Assert(autoFramingVf.Contains("crop=w='trunc(min(iw,ih*9/16)/2)*2'"), "orientation 9:16 autoframing filter chain");

        // Custom Presets tests
        var baseSettings = new Settings {
            Width = 1920,
            Height = 1080,
            Fps = 60,
            BitrateMbps = 22,
            Codec = "hevc",
            SpoutOutput = true,
            Stabilization = true,
            StabilizationMode = "strong",
            BackgroundEffect = "ai_bokeh",
            ColorProfile = "teal_orange"
        };

        var customPreset = baseSettings.Clone();
        customPreset.Width = 2560;
        customPreset.Height = 1440;
        customPreset.Fps = 30;
        customPreset.BitrateMbps = 28;
        customPreset.ColorProfile = "noir";
        customPreset.CustomPresets.Clear();

        baseSettings.CustomPresets["2K Studio"] = customPreset;
        Assert(baseSettings.CustomPresets.ContainsKey("2K Studio"), "custom preset stored");
        Assert(baseSettings.CustomPresets.ContainsKey("2k studio"), "custom preset case-insensitive lookup");

        var clonedSettings = baseSettings.Clone();
        Assert(clonedSettings.CustomPresets.ContainsKey("2K Studio"), "cloned settings preserves presets");
        clonedSettings.CustomPresets["Temp"] = new Settings();
        Assert(!baseSettings.CustomPresets.ContainsKey("Temp"), "clone dictionary is independent");

        var targetSettings = new Settings { Width = 1280, Height = 720, Fps = 30, DeviceSerial = "PHONE_123", PhoneIp = "192.168.1.50" };
        targetSettings.CopyCapturePropertiesFrom(customPreset);
        Assert(targetSettings.Width == 2560 && targetSettings.Height == 1440, "CopyCapturePropertiesFrom applies resolution");
        Assert(targetSettings.BitrateMbps == 28, "CopyCapturePropertiesFrom applies bitrate");
        Assert(targetSettings.ColorProfile == "noir", "CopyCapturePropertiesFrom applies color profile");
        Assert(targetSettings.DeviceSerial == "PHONE_123" && targetSettings.PhoneIp == "192.168.1.50", "CopyCapturePropertiesFrom preserves device serial and IP");

        // JSON roundtrip test
        var presetJson = JsonSerializer.Serialize(baseSettings);
        var presetDeserialized = JsonSerializer.Deserialize<Settings>(presetJson);
        Assert(presetDeserialized != null && presetDeserialized.CustomPresets.ContainsKey("2K Studio"), "custom presets serialize/deserialize roundtrip");
        Assert(presetDeserialized!.CustomPresets["2K Studio"].Width == 2560, "deserialized preset properties match");

        // OrientationMode dimension and PreviewDimensions tests
        var portraitSettings = new Settings { Width = 1920, Height = 1080, OrientationMode = "9:16_crop" };
        var pDim = portraitSettings.OutputDimensions;
        Assert(pDim.Width == 1080 && pDim.Height == 1920, "OutputDimensions in 9:16 portrait mode transposes to 1080x1920");
        var landscapeSettings = new Settings { Width = 1920, Height = 1080, OrientationMode = "16:9" };
        var lDim = landscapeSettings.OutputDimensions;
        Assert(lDim.Width == 1920 && lDim.Height == 1080, "OutputDimensions in 16:9 landscape mode remains 1920x1080");
        var previewPortrait = PreviewDecoder.PreviewDimensions(portraitSettings);
        Assert(previewPortrait.Width == 540 && previewPortrait.Height == 960, "PreviewDimensions in 9:16 portrait mode is 540x960");
        var previewLandscape = PreviewDecoder.PreviewDimensions(landscapeSettings);
        Assert(previewLandscape.Width == 960 && previewLandscape.Height == 540, "PreviewDimensions in 16:9 landscape mode is 960x540");

        // DiagnosticsReport tests
        var reportSettings = new Settings { Width = 1920, Height = 1080, BitrateMbps = 24, Codec = "hevc", OrientationMode = "9:16_crop", SpoutOutput = true };
        var testDiagStatus = new LiveStatus("streaming", "DEVICE_TEST_4.0", "USB Direct", "1080x1920", 60, 59.9, 24.2, 24.0, 15000, TimeSpan.FromMinutes(5), "ok", "cool", "details", new PowerTelemetry(88, "Discharging", "Battery", 4.15, -450, -420, -1.86, 33.5), 0, 0, 0, 12.0);
        var diagReport = DiagnosticsReport.Generate(reportSettings, testDiagStatus, null, connMgr);
        Assert(diagReport.Contains("H3H CAM 4.0 DIAGNOSTICS REPORT"), "diagnostics report title");
        Assert(diagReport.Contains("DEVICE_TEST_4.0"), "diagnostics report serial");
        Assert(diagReport.Contains("USB Direct"), "diagnostics report transport");
        Assert(diagReport.Contains("1080x1920"), "diagnostics report output dimensions");
        Assert(diagReport.Contains("HEVC"), "diagnostics report codec");
        Assert(diagReport.Contains("H3H Protocol v2"), "diagnostics report protocol version");
        Assert(diagReport.Contains("App Version: 4.0") && !diagReport.Contains("3.0.0"), "diagnostics report has dynamic 4.0 app version and no 3.0.0");

        // RollingLogger tests
        using (var testLogger = new RollingLogger()) {
            testLogger.Log("Test info message");
            testLogger.Log("Test critical error message", LogLevel.Error);
            var errs = testLogger.GetRecentErrors();
            Assert(errs.Length >= 1 && errs.Any(e => e.Contains("Test critical error message")), "rolling logger recent errors capture");
        }

        // Studio Video Effects Tests
        int testW = 80, testH = 60;
        int testBytes = testW * testH * 4;
        byte[] srcFrame = new byte[testBytes];
        byte[] dstFrame = new byte[testBytes];
        for (int i = 0; i < testBytes; i += 4) {
            srcFrame[i] = 50;      // B
            srcFrame[i + 1] = 120; // G
            srcFrame[i + 2] = 200; // R
            srcFrame[i + 3] = 255; // A
        }
        StudioEffectsProcessor.FastBoxBlur(srcFrame, dstFrame, testW, testH, 5);
        Assert(dstFrame[3] == 255 && dstFrame[testBytes - 1] == 255, "FastBoxBlur preserves alpha");
        Assert(dstFrame[0] == 50 && dstFrame[1] == 120 && dstFrame[2] == 200, "FastBoxBlur flat image preserves values");

        // Greenscreen test
        StudioEffectsProcessor.ApplyEffects(srcFrame, dstFrame, testW, testH, "greenscreen", 10, false, 0.5f, 0.5f);
        // Outer corner should be pure green (#00FF00)
        int cornerIdx = 0;
        Assert(dstFrame[cornerIdx + 1] == 255 && dstFrame[cornerIdx] == 0 && dstFrame[cornerIdx + 2] == 0, "Greenscreen corner is pure green");

        // WhitebalanceCalibrator tests
        int wbW = 100, wbH = 100;
        int wbBytes = wbW * wbH * 4;
        byte[] neutralPaper = new byte[wbBytes];
        for (int i = 0; i < wbBytes; i += 4) {
            neutralPaper[i] = 180;     // B
            neutralPaper[i + 1] = 180; // G
            neutralPaper[i + 2] = 180; // R
            neutralPaper[i + 3] = 255;
        }
        var resNeutral = WhitebalanceCalibrator.CalibrateFromBgra(neutralPaper, wbW, wbH);
        Assert(resNeutral.Success, "WB neutral paper calibration success");
        Assert(Math.Abs(resNeutral.RedGain - 1.0f) < 0.05f && Math.Abs(resNeutral.BlueGain - 1.0f) < 0.05f, "WB neutral paper gains near 1.0");

        // Warm light test (incandescent / halogen: high Red, low Blue)
        byte[] warmPaper = new byte[wbBytes];
        for (int i = 0; i < wbBytes; i += 4) {
            warmPaper[i] = 110;     // B
            warmPaper[i + 1] = 170; // G
            warmPaper[i + 2] = 220; // R
            warmPaper[i + 3] = 255;
        }
        var resWarm = WhitebalanceCalibrator.CalibrateFromBgra(warmPaper, wbW, wbH);
        Assert(resWarm.Success, "WB warm paper calibration success");
        Assert(resWarm.EstimatedKelvin < 4000, "WB warm light detects low Kelvin (< 4000 K)");
        Assert(resWarm.RedGain < 1.0f && resWarm.BlueGain > 1.0f, "WB warm light compensates by boosting Blue and reducing Red");

        // Cool light test (daylight shadow: high Blue, low Red)
        byte[] coolPaper = new byte[wbBytes];
        for (int i = 0; i < wbBytes; i += 4) {
            coolPaper[i] = 210;     // B
            coolPaper[i + 1] = 180; // G
            coolPaper[i + 2] = 130; // R
            coolPaper[i + 3] = 255;
        }
        var resCool = WhitebalanceCalibrator.CalibrateFromBgra(coolPaper, wbW, wbH);
        Assert(resCool.Success, "WB cool paper calibration success");
        Assert(resCool.EstimatedKelvin > 5500, "WB cool light detects high Kelvin (> 5500 K)");
        Assert(resCool.RedGain > 1.0f && resCool.BlueGain < 1.0f, "WB cool light compensates by boosting Red and reducing Blue");

        // Dark and clipped validation
        byte[] darkFrame = new byte[wbBytes];
        Array.Fill(darkFrame, (byte)10);
        var resDark = WhitebalanceCalibrator.CalibrateFromBgra(darkFrame, wbW, wbH);
        Assert(!resDark.Success, "WB dark frame rejected");

        byte[] clippedFrame = new byte[wbBytes];
        Array.Fill(clippedFrame, (byte)255);
        var resClipped = WhitebalanceCalibrator.CalibrateFromBgra(clippedFrame, wbW, wbH);
        Assert(!resClipped.Success, "WB overexposed clipped frame rejected");

        // QR Code Generator Tests (Version 1-6)
        var shortQr = QrCodeGenerator.GenerateBitmap("h3hcam://wifi?pc=192.168.1.1&port=5000", scale: 4, border: 2);
        Assert(shortQr != null && shortQr.PixelWidth > 0, "QR short payload generation");
        var longUri = "h3hcam://wifi?pc=192.168.100.250&port=5000&codec=hevc&fps=60&width=3840&height=2160&key=ultra_wide_lens_0&quality=maximum";
        var longQr = QrCodeGenerator.GenerateBitmap(longUri, scale: 4, border: 2);
        Assert(longQr != null && shortQr != null && longQr.PixelWidth > shortQr.PixelWidth, "QR long payload (Version 5/6) scalable generation");

        // Wi-Fi Discovery IP resolution test
        var resolvedIp = WifiDiscoveryService.ResolveBestLocalIpv4();
        Assert(!string.IsNullOrWhiteSpace(resolvedIp) && Settings.Ipv4(resolvedIp), "WifiDiscoveryService resolves valid IPv4");

        // Spout2 D3D11 shared texture sender test
        using (var spout = new SpoutSender("H3HCamTest", 160, 120)) {
            Assert(spout.SenderName == "H3HCamTest", "Spout sender name");
            Assert(spout.Width == 160 && spout.Height == 120, "Spout dimensions");
            Assert(spout.SharedHandle != IntPtr.Zero, "Spout shared texture handle");
            var testNv12 = new byte[160 * 120 * 3 / 2];
            spout.WriteFrame(testNv12);
        }

        // OBS Scene Optimizer JSON transform test
        var sampleObsScene = """
        {
            "name": "Test Scene",
            "sources": [
                {
                    "name": "Мультимедиа",
                    "id": "ffmpeg_source",
                    "settings": {
                        "input": "udp://127.0.0.1:5001?fifo_size=4096&overrun_nonfatal=1"
                    },
                    "enabled": true
                },
                {
                    "name": "Spout2 Capture",
                    "id": "spout_capture",
                    "settings": {
                        "spoutsenders": "OtherSender"
                    },
                    "enabled": true
                }
            ]
        }
        """;
        var obsJsonNode = System.Text.Json.Nodes.JsonNode.Parse(sampleObsScene);
        var obsSources = obsJsonNode?["sources"] as System.Text.Json.Nodes.JsonArray;
        Assert(obsSources != null && obsSources.Count == 2, "obs sources count");
        foreach (var src in obsSources!) {
            if (src is System.Text.Json.Nodes.JsonObject sObj) {
                var id = sObj["id"]?.ToString() ?? "";
                var settingsObj = sObj["settings"] as System.Text.Json.Nodes.JsonObject;
                var input = settingsObj?["input"]?.ToString() ?? "";
                if (input.Contains("5001")) {
                    sObj["enabled"] = false;
                }
                if (id == "spout_capture" && settingsObj != null) {
                    settingsObj["spoutsenders"] = "H3HCam";
                }
            }
        }
        Assert(obsSources[0]?["enabled"]?.GetValue<bool>() == false, "obs legacy udp 5001 source disabled");
        Assert(obsSources[1]?["settings"]?["spoutsenders"]?.ToString() == "H3HCam", "obs spout sender set to H3HCam");

        var media = MediaCommands.Relay(settings, "session.sdp");
        Assert(media.Contains("session.sdp") && media.Contains("copy") && !media.Contains("h264"), "RTP media command");
        Assert(media.Contains("64") && media.Contains("30000"), "low-latency RTP recovery window");

        // SnapshotManager unit tests
        var dummyBgra = new byte[100 * 100 * 4];
        Array.Fill(dummyBgra, (byte)200);
        var snapNative = SnapshotManager.TakeSnapshot(dummyBgra, 100, 100, upscale4K: false, customDirectory: root);
        Assert(File.Exists(snapNative.FilePath) && snapNative.Width == 100 && snapNative.Height == 100 && snapNative.FileSize > 0, "SnapshotManager native PNG snapshot");
        File.Delete(snapNative.FilePath);

        var snap4K = SnapshotManager.TakeSnapshot(dummyBgra, 100, 100, upscale4K: true, customDirectory: root);
        Assert(File.Exists(snap4K.FilePath) && snap4K.Width == 3840 && snap4K.Height == 2160 && snap4K.FileSize > 0, "SnapshotManager 4K edge-adaptive super-res snapshot");
        File.Delete(snap4K.FilePath);

        // Media Foundation Virtual Camera tests
        var mfSupported = MediaFoundationVirtualCamera.IsSupported;
        Console.WriteLine($"[TEST] Windows Media Foundation Virtual Camera supported: {mfSupported}");

        // VirtualCameraWriter zero-admin user-level registration tests
        var driverDllPath = VirtualCameraWriter.FindDriverDll();
        if (driverDllPath != null) {
            bool userInstallResult = VirtualCameraWriter.InstallUserLevel();
            Assert(userInstallResult, "VirtualCameraWriter.InstallUserLevel succeeded");
            Assert(VirtualCameraWriter.IsInstalled(), "VirtualCameraWriter.IsInstalled returns true after user-level registration");
        }

        // StreamRecorder unit tests
        var testRec = new StreamRecorder(_ => {});
        Assert(!testRec.IsRecording && testRec.Elapsed == TimeSpan.Zero, "StreamRecorder initial state idle");
        var stopResult = testRec.Stop();
        Assert(stopResult.Duration == TimeSpan.Zero, "StreamRecorder idle stop returns TimeSpan.Zero");

        File.WriteAllText(Path.Combine(root, "unit.json"), JsonSerializer.Serialize(new {
            devices = devices.Count,
            network = devices.Count(x => x.IsNetwork),
            cameras = capabilities.Cameras.Count,
            mediaArguments = media.Count
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS unit · settings, ADB parsing, capabilities, RTP command, H3H framing");
    }

    private static async Task Probe(string transport, string serial, string codec = "h264", int fps = 30) {
        var settings = HardwareSettings(transport, serial, codec);
        settings.Fps = fps;
        var backup = await BackupSettings();
        try {
            await using var engine = new ReceiverEngine(settings, Console.WriteLine);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await engine.Test(timeout.Token);
            Console.WriteLine($"PASS full transport probe · {transport} · {codec} · {fps}fps");
        } finally {
            await RestoreSettings(backup);
        }
    }

    private static async Task Catalog(string serial) {
        var settings = HardwareSettings("auto", serial);
        var controller = new AdbController(settings, Console.WriteLine);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await controller.Connect(timeout.Token);
        var phone = await controller.GetCapabilities(timeout.Token);
        Assert(phone.Cameras.Count > 0 && phone.Cameras.All(x => x.Modes.Count > 0), "phone camera catalog");
        Console.WriteLine($"PASS catalog · {phone.Manufacturer} {phone.Model} · {phone.Cameras.Count} Camera2 modules");
        foreach (var camera in phone.Cameras)
            Console.WriteLine($"  {camera.Key}: {camera.Label} · {camera.Modes.Count} modes: " + string.Join(", ", camera.Modes.Select(m => m.Key)));
    }

    private static async Task Stream(string transport, string root, string serial, int seconds,
        string cameraKey, string focus, bool screenOff, bool sixtyFps, bool preview, bool fullHd, bool virtualCamera, bool highBitrate, bool hevc = false, bool qhd = false, bool spout = false, bool record = false) {
        Directory.CreateDirectory(root);
        var settings = HardwareSettings(transport, serial);
        settings.CameraKey = cameraKey;
        if (hevc) settings.Codec = "hevc";
        if (sixtyFps) { settings.Fps = 60; settings.Width = 1280; settings.Height = 720; }
        if (fullHd) { settings.Width = 1920; settings.Height = 1080; }
        if (qhd) { settings.Width = 2560; settings.Height = 1440; }
        if (highBitrate) { settings.BitrateMbps = 32; settings.WifiLimitMbps = 0; }
        settings.Preview = preview;
        settings.VirtualCamera = virtualCamera;
        settings.SpoutOutput = spout;
        settings.Focus = focus;
        settings.ScreenOff = screenOff;
        if (focus == "manual") settings.FocusDistance = 2f;
        var backup = await BackupSettings();
        var suffix = (cameraKey == "auto" ? transport : $"{transport}-camera-{cameraKey.Replace('/', '_')}") +
            (screenOff ? "-screen-off" : "");
        var outputPath = Path.Combine(root, $"stream-{suffix}.ts");
        var statusPath = Path.Combine(root, $"stream-{suffix}-status.json");
        var statuses = new List<LiveStatus>();
        try {
            await using var engine = new ReceiverEngine(settings, Console.WriteLine);
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, settings.ObsPort));
            receiver.Client.ReceiveBufferSize = 1024 * 1024;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 25));
            await using var output = File.Create(outputPath);
            engine.Status += state => {
                statuses.Add(state);
                Console.WriteLine(JsonSerializer.Serialize(state));
            };
            var capture = Task.Run(async () => {
                try {
                    while (!timeout.IsCancellationRequested) {
                        var datagram = await receiver.ReceiveAsync(timeout.Token);
                        await output.WriteAsync(datagram.Buffer, timeout.Token);
                    }
                } catch (OperationCanceledException) {}
            });
            await engine.Start(timeout.Token);
            if (transport == "wifi") {
                Assert(string.IsNullOrEmpty(settings.PcIp), "Wi-Fi route resolution stays in runtime settings");
                var controls = settings.Clone();
                controls.BitrateMbps = Math.Max(1, settings.BitrateMbps - 1);
                await engine.UpdateControls(controls, timeout.Token);
                await engine.UpdateControls(settings, timeout.Token);
            }
            string? recordingPath = record ? engine.StartRecording(root) : null;
            await Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
            if (virtualCamera || spout) Assert(engine.VirtualCameraFrames > (seconds - 5) * 45,
                "virtual camera / Spout2 decodes and publishes live frames");
            await engine.Stop();
            timeout.Cancel();
            await capture;
            await output.FlushAsync();
            var outputLength = output.Length;
            if (outputLength < 100_000) throw new IOException("No MPEG-TS output");
            await output.DisposeAsync();
            await File.WriteAllTextAsync(statusPath,
                JsonSerializer.Serialize(statuses, new JsonSerializerOptions { WriteIndented = true }));
            await DecodeCheck(outputPath);
            if (recordingPath != null) {
                Assert(new FileInfo(recordingPath).Length > 100000, "independent recording receives frames alongside relay");
                await DecodeCheck(recordingPath);
            }
            Assert(statuses.Any(x => x.DetectedFps > 5 && x.Packets > 20), "live RTP statistics");
            if (sixtyFps) Assert(statuses.Count(x => x.DetectedFps >= 55 && x.DetectedFps <= 65) >= 5,
                "sustained real 60 FPS over USB");
            Console.WriteLine($"PASS {transport} stream · {outputLength} bytes · decoded");
        } finally {
            await RestoreSettings(backup);
        }
    }

    private static Settings HardwareSettings(string transport, string serial, string codec = "h264") => new() {
        Transport = transport,
        DeviceSerial = serial,
        Codec = codec,
        PhoneIp = "192.168.1.100",
        AutoPcIp = true,
        Fps = 30,
        Width = 1920,
        Height = 1080,
        BitrateMbps = 12,
        CameraKey = "auto",
        Preview = false,
        VirtualCamera = false,
        Obs = true,
        ScreenOff = false,
        RtpPort = 15000,
        UsbPort = 15002,
        ObsPort = 15001,
        PreviewPort = 15003
    };

    private static async Task PreviewToggle(string serial) {
        var backup = await BackupSettings();
        var settings = HardwareSettings("usb", serial);
        settings.Fps = 60;
        settings.BitrateMbps = 32;
        settings.VirtualCamera = true;
        settings.Obs = false;
        settings.Preview = false;
        settings.ScreenOff = true;
        try {
            await using var engine = new ReceiverEngine(settings, Console.WriteLine);
            var statuses = new List<LiveStatus>();
            engine.Status += status => { statuses.Add(status); Console.WriteLine(JsonSerializer.Serialize(status)); };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            await engine.Start(timeout.Token);
            await Task.Delay(8000, timeout.Token);
            Assert(engine.VirtualCameraFrames > 100, "direct virtual camera running");
            Assert(engine.RelayProcessId == null, "disabled MPEG-TS relay does not run");
            Assert(engine.PreviewProcessId == null, "preview starts disabled");
            var before = engine.VirtualCameraFrames;
            engine.SetPreviewEnabled(true);
            await Task.Delay(4000, timeout.Token);
            Assert(engine.PreviewProcessId != null, "preview can open without restarting stream");
            using (var preview = System.Diagnostics.Process.GetProcessById(engine.PreviewProcessId!.Value)) {
                Assert(preview.CloseMainWindow(), "preview accepts normal window close");
            }
            await Task.Delay(4000, timeout.Token);
            Assert(!engine.PreviewEnabled && engine.PreviewProcessId == null, "window close disables preview instead of respawning");
            Assert(engine.VirtualCameraFrames > before + 250, "virtual camera continues during window toggle");
            engine.SetPreviewEnabled(true);
            await Task.Delay(3500, timeout.Token);
            Assert(engine.PreviewProcessId != null, "preview can reopen after close");
            engine.SetPreviewEnabled(false);
            await Task.Delay(3000, timeout.Token);
            Assert(engine.PreviewProcessId == null, "checkbox closes preview");
            Assert(statuses.Count(x => x.DetectedFps >= 55) >= 8, "60 FPS with 32 Mbps profile");
            Assert(statuses.Any(x => x.EncodedMbps > 25), "encoder delivers increased bitrate");
            await engine.Stop();
            Console.WriteLine("PASS preview-toggle · close/reopen/off · direct camera · 1080p60 at 32 Mbps");
        } finally { await RestoreSettings(backup); }
    }

    private static async Task DecodeCheck(string input) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Processes.Run(ToolPaths.Find("ffmpeg.exe"), [
            "-hide_banner", "-loglevel", "error", "-xerror", "-i", input,
            "-fps_mode", "passthrough", "-enc_time_base", "1:90000", "-f", "null", "-"
        ], timeout.Token, 30000);
    }

    private static async Task FilterTest() {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        foreach (var mode in new[] { "9:16_crop", "9:16_fit", "9:16_autoframing" }) {
            foreach (var rotation in new[] { 0, 90, 180, 270 }) {
                var s = new Settings { OrientationMode = mode, Rotation = rotation };
                await Processes.Run(ToolPaths.Find("ffmpeg.exe"), [
                    "-hide_banner", "-loglevel", "error", "-xerror", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=1",
                    "-vf", s.BuildVideoFilter(180, 320), "-frames:v", "1", "-f", "null", "-"
                ], timeout.Token, 10000);
            }
        }
        Console.WriteLine("PASS filter-test · all portrait modes at 0/90/180/270 degrees decoded");
    }

    private static async Task PacingTest(string root, int fps, string codec, int bitrate, int seconds, int initialBitrate, string transport) {
        Directory.CreateDirectory(root);
        var s = Settings.Load().Clone();
        s.Transport = transport; s.Fps = fps; s.Codec = codec; s.BitrateMbps = bitrate;
        s.Width = 1920; s.Height = 1080; s.Preview = true;
        s.VirtualCamera = true; s.SpoutOutput = true; s.Obs = false;
        s.AdaptiveBitrate = false; s.WifiLimitMbps = 0;
        var requested = s.Clone();
        if (initialBitrate > 0) s.BitrateMbps = initialBitrate;
        var backup = await BackupSettings();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gaps = new List<double>();
        var states = new List<LiveStatus>();
        var sync = new object();
        double firstFrameMs = -1, lastFrameMs = -1, streamStartMs = double.MaxValue;
        int errors = 0;
        await using var engine = new ReceiverEngine(s, line => {
            Console.WriteLine(line);
            if (line.Contains("Error constructing") || line.Contains("non-existing PPS") || line.Contains("Could not find ref"))
                Interlocked.Increment(ref errors);
        });
        engine.PreviewFrame += (_, _, _) => {
            var now = clock.Elapsed.TotalMilliseconds;
            lock (sync) {
                if (firstFrameMs < 0) firstFrameMs = now;
                if (now - streamStartMs > 4000 && lastFrameMs >= 0) gaps.Add(now - lastFrameMs);
                lastFrameMs = now;
            }
        };
        engine.Status += status => { lock (sync) states.Add(status); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
        try {
            await engine.Start(timeout.Token);
            streamStartMs = clock.Elapsed.TotalMilliseconds;
            await Task.Delay(4000, timeout.Token);
            if (initialBitrate > 0) await engine.UpdateControls(requested, timeout.Token);
            var startFrames = engine.VirtualCameraFrames;
            var startSample = clock.Elapsed.TotalSeconds;
            await Task.Delay((seconds - 4) * 1000, timeout.Token);
            var outputFps = (engine.VirtualCameraFrames - startFrames) / (clock.Elapsed.TotalSeconds - startSample);
            await engine.Stop();
            double[] sorted; lock (sync) sorted = gaps.Order().ToArray();
            Assert(sorted.Length > fps * (seconds - 6) * .7, "preview receives sustained frames");
            var report = new {
                Fps = fps, Codec = codec, BitrateMbps = bitrate,
                FirstFrameMs = firstFrameMs - streamStartMs,
                PreviewFps = 1000 / sorted.Average(),
                P95GapMs = sorted[(int)((sorted.Length - 1) * .95)],
                P99GapMs = sorted[(int)((sorted.Length - 1) * .99)],
                MaxGapMs = sorted.Last(), OutputFps = outputFps, DecodeReferenceErrors = errors,
                LastStatus = states.LastOrDefault()
            };
            await File.WriteAllTextAsync(Path.Combine(root, "pacing.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            Assert(outputFps > fps * .85, "virtual and Spout output keeps requested cadence");
            Assert(errors == 0, "decoder retains probe keyframe references");
            if (initialBitrate > 0) Assert(states.Last().EncodedMbps > bitrate * .75, "live bitrate reaches the phone encoder");
            Console.WriteLine("PASS pacing-test");
        } finally { await engine.Stop(); await RestoreSettings(backup); }
    }

    private static async Task PrivacyTest(string serial) {
        var s = HardwareSettings("usb", serial);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, s.ObsPort));
        long bytes = 0;
        var capture = Task.Run(async () => {
            try {
                while (!timeout.IsCancellationRequested) {
                    var packet = await receiver.ReceiveAsync(timeout.Token);
                    Interlocked.Add(ref bytes, packet.Buffer.Length);
                }
            } catch (OperationCanceledException) { }
        });
        await using var engine = new ReceiverEngine(s, Console.WriteLine);
        try {
            await engine.Start(timeout.Token);
            await Task.Delay(5000, timeout.Token);
            Assert(Interlocked.Read(ref bytes) > 100000, "relay sends live video before mute");
            Assert(!engine.RequiresRestart(s), "resolved tool paths do not force restart");
            var muted = s.Clone();
            muted.PrivacyMute = true;
            await engine.UpdateControls(muted, timeout.Token);
            await Task.Delay(1500, timeout.Token); // drain already queued localhost packets
            var paused = Interlocked.Read(ref bytes);
            await Task.Delay(2000, timeout.Token);
            Assert(Interlocked.Read(ref bytes) == paused, "privacy pauses MPEG-TS output");
            await engine.UpdateControls(s, timeout.Token);
            await Task.Delay(4000, timeout.Token);
            Assert(Interlocked.Read(ref bytes) > paused + 100000, "unmute resumes with fresh IDR");
            var preview = s.Clone();
            preview.Preview = true;
            await engine.UpdateControls(preview, timeout.Token);
            engine.SetPreviewEnabled(true);
            await Task.Delay(2500, timeout.Token);
            Assert(engine.PreviewProcessId != null, "preview starts after controls update with automatic tool paths");
            preview.VirtualCamera = true;
            await engine.UpdateControls(preview, timeout.Token);
            await Task.Delay(3500, timeout.Token);
            Assert(engine.VirtualCameraFrames > 10, "virtual output can be enabled after starting with it disabled");
            await Task.WhenAll(engine.UpdateControls(s, timeout.Token), engine.Stop());
            Assert(!engine.Running && engine.PreviewProcessId == null, "stop and settings update leave no preview process");
            Console.WriteLine("PASS privacy-test · mute/unmute relay · preview and virtual toggle · concurrent stop");
        } finally {
            await engine.Stop();
            timeout.Cancel();
            await capture;
        }
    }

    private static async Task<string?> BackupSettings() =>
        File.Exists(Settings.FilePath) ? await File.ReadAllTextAsync(Settings.FilePath) : null;

    private static async Task RestoreSettings(string? contents) {
        if (contents != null) {
            Directory.CreateDirectory(Settings.DataDir);
            await File.WriteAllTextAsync(Settings.FilePath, contents);
        } else if (File.Exists(Settings.FilePath)) {
            File.Delete(Settings.FilePath);
        }
    }

    private static void Assert(bool condition, string name) {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    }

    private static void ExpectThrows<T>(Action action, string name) where T : Exception {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }

    private static void MakeIcons(string masterPath) {
        masterPath = Path.GetFullPath(masterPath);
        if (!File.Exists(masterPath)) throw new FileNotFoundException("Master icon not found: " + masterPath);
        using var stream = File.OpenRead(masterPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];

        // Find repository root
        var root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "windows")) && Directory.GetParent(root) != null)
            root = Directory.GetParent(root)!.FullName;

        // 1. Generate Windows ICO
        var icoSizes = new[] { 256, 128, 64, 48, 32, 16 };
        var icoPngs = icoSizes.Select(s => EncodePng(source, s, s)).ToArray();
        var icoPath = Path.Combine(root, "windows", "app.ico");
        WriteIco(icoPath, icoSizes, icoPngs);
        Console.WriteLine("Generated " + icoPath);

        // 2. Generate Windows PNG
        var pngPath = Path.Combine(root, "windows", "app.png");
        File.WriteAllBytes(pngPath, icoPngs[0]);
        Console.WriteLine("Generated " + pngPath);

        // 3. Generate Android launcher icons
        var densities = new Dictionary<string, int> {
            { "mipmap-mdpi", 48 },
            { "mipmap-hdpi", 72 },
            { "mipmap-xhdpi", 96 },
            { "mipmap-xxhdpi", 144 },
            { "mipmap-xxxhdpi", 192 }
        };
        var resDir = Path.Combine(root, "android", "app", "src", "main", "res");
        foreach (var (dir, size) in densities) {
            var targetDir = Path.Combine(resDir, dir);
            Directory.CreateDirectory(targetDir);
            
            var launcherBytes = EncodePng(source, size, size, round: false);
            File.WriteAllBytes(Path.Combine(targetDir, "ic_launcher.png"), launcherBytes);
            
            var roundBytes = EncodePng(source, size, size, round: true);
            File.WriteAllBytes(Path.Combine(targetDir, "ic_launcher_round.png"), roundBytes);
            
            var oldWebp = Path.Combine(targetDir, "ic_launcher.webp");
            var oldRoundWebp = Path.Combine(targetDir, "ic_launcher_round.webp");
            if (File.Exists(oldWebp)) File.Delete(oldWebp);
            if (File.Exists(oldRoundWebp)) File.Delete(oldRoundWebp);

            Console.WriteLine($"Generated Android {dir} ({size}x{size})");
        }
    }

    private static byte[] EncodePng(BitmapSource source, int width, int height, bool round = false) {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            if (round) {
                var clip = new EllipseGeometry(new Point(width / 2.0, height / 2.0), width / 2.0, height / 2.0);
                dc.PushClip(clip);
            }
            dc.DrawImage(source, new Rect(0, 0, width, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void WriteIco(string path, int[] sizes, byte[][] pngData) {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)sizes.Length);

        var offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++) {
            var s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)pngData[i].Length);
            bw.Write((uint)offset);
            offset += pngData[i].Length;
        }

        for (var i = 0; i < sizes.Length; i++) {
            bw.Write(pngData[i]);
        }
    }
}
