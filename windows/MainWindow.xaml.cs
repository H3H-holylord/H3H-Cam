using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Win32;

namespace S8Cam;

public partial class MainWindow : Window {
    private sealed record Choice(string Key, string RawLabel) {
        public string Label => L.PhoneText(L.Relocalize(RawLabel));
        public override string ToString() => Label;
    }
    private sealed record FpsChoice(int Value) {
        public string Label => $"{Value} fps";
        public override string ToString() => Label;
    }

    private Settings settings;
    private readonly bool safeMode=Environment.GetCommandLineArgs().Contains("--safe-mode");
    private ReceiverEngine? engine;
    private PhoneCapabilities? capabilities;
    private bool busy, closing, closed, filling, forceExit, customPresetsFilling;
    private CancellationTokenSource? operation;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> logs = new();
    private readonly DispatcherTimer logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly string[] focusValues = ["continuous", "auto", "infinity", "manual"];
    private readonly string[] wbValues = ["auto", "daylight", "cloudy", "incandescent", "fluorescent"];
    private readonly PowerHistory powerHistory = new();
    private PowerTelemetry lastPower = new();
    private LiveStatus? lastStatus;
    private TrayIcon? trayIcon;
    private readonly DispatcherTimer controlsDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ThermalGuard? thermalGuard;
    private readonly DispatcherTimer usbWatcherTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private System.Windows.Media.Imaging.WriteableBitmap? previewBitmap;
    private PreviewWindow? previewWindow;
    private bool gridEnabled;
    private bool focusPeakingEnabled;
    private bool zebraEnabled;
    private bool faceOverlayEnabled = true;
    private StreamHudWindow? hudWindow;
    private byte[]? processedPreviewBuffer;
    private byte[]? overlayBuffer;
    private WifiDiscoveryService? wifiDiscovery;
    private volatile bool previewRenderingVisible;
    private TaskCompletionSource? requestedSnapshotFrame;
    private void UpdatePreviewActivity() {
        previewRenderingVisible = (IsVisible && WindowState != WindowState.Minimized) ||
            (previewWindow?.IsVisible == true && previewWindow.WindowState != WindowState.Minimized);
        engine?.SetPreviewActivity(previewRenderingVisible || requestedSnapshotFrame != null);
    }
    private int renderingFrame = 0;
    private readonly byte[][] previewUiBuffers = new byte[2][];
    private int previewUiBufIndex = 0;
    private readonly object previewProcessLock = new();
    private byte[]? latestRawPreviewFrame;
    private byte[]? rawRenderBuffer;
    private int latestRawPreviewW, latestRawPreviewH;
    private readonly object latestRawPreviewLock = new();
    private readonly DispatcherTimer wbOverlayTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

    public MainWindow() {
        filling = true;
        settings = Settings.Load();
        L.Configure(settings.Language);
        InitializeComponent();
        Title = "H3H Cam " + typeof(App).Assembly.GetName().Version?.ToString(3);
        LanguageChoice.SelectedIndex = settings.Language switch { "ru" => 1, "en" => 2, _ => 0 };
        IsVisibleChanged += (_, _) => UpdatePreviewActivity();
        wbOverlayTimer.Tick += (_, _) => {
            wbOverlayTimer.Stop();
            if (WbTargetBox != null) WbTargetBox.Visibility = Visibility.Collapsed;
        };
        if (System.Windows.Application.Current != null) {
            System.Windows.Application.Current.SessionEnding += (_, _) => { forceExit = true; };
        }
        if (settings.WindowWidth >= 800 && settings.WindowHeight >= 600) {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0) {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        Fill();
        UpdateModeUI();
        UpdateVirtualCamStatus();
        wifiDiscovery = new WifiDiscoveryService(() => Volatile.Read(ref settings).Clone(), Log);
        wifiDiscovery.PhoneDiscovered += phone => Dispatcher.BeginInvoke(() => {
            if (closing || closed) return;
            Log(L.Format("s_0c10be76de5c", phone.Model, phone.Ip));
            if (string.IsNullOrWhiteSpace(PhoneIp.Text) || PhoneIp.Text == "192.168.1.100") {
                PhoneIp.Text = phone.Ip;
                QueueSave();
            }
        });
        if(!safeMode)wifiDiscovery.Start();
        LivePreviewImage.SizeChanged += (_, _) => { if (gridEnabled) RedrawGrid(); };
        SettingsPanel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueSave()));
        SettingsPanel.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueSave()));
        SettingsPanel.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        SettingsPanel.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        SettingsPanel.AddHandler(System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((_, _) => QueueSave()));
        BasicPanel.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        BasicPanel.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => QueueSave()));
        BasicPanel.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueSave()));
        FaceTracking.Checked += (_, _) => UpdateFaceTrackingUI(true);
        FaceTracking.Unchecked += (_, _) => UpdateFaceTrackingUI(false);
        AutoFraming.Checked += (_, _) => UpdateAutoFramingUI(true);
        AutoFraming.Unchecked += (_, _) => UpdateAutoFramingUI(false);
        LockAeAwb.Checked += (_, _) => UpdateLockAeAwbUI(true);
        LockAeAwb.Unchecked += (_, _) => UpdateLockAeAwbUI(false);
        Stabilization.Checked += (_, _) => {
            if (BasicStabilizationCheck != null && BasicStabilizationCheck.IsChecked != true)
                BasicStabilizationCheck.IsChecked = true;
        };
        Stabilization.Unchecked += (_, _) => {
            if (BasicStabilizationCheck != null && BasicStabilizationCheck.IsChecked != false)
                BasicStabilizationCheck.IsChecked = false;
        };
        saveTimer.Tick += (_, _) => {
            saveTimer.Stop();
            try {
                var current = Read();
                current.Save();
                UpdateRunOnStartup(current.RunOnStartup);
            }
            catch (Exception ex) { Log(L.Get("s_b3f65a721581") + ex.Message); }
        };
        controlsDebounceTimer.Tick += (_, _) => {
            controlsDebounceTimer.Stop();
            if (engine?.Running == true) {
                var current = Read();
                thermalGuard?.UpdateSettings(current);
                _ = ApplyControlsSafely(current);
            }
        };
        logTimer.Tick += (_, _) => {
            var batch = new List<string>();
            for (var i = 0; i < 100 && logs.TryDequeue(out var line); i++) batch.Add(line);
            if (batch.Count > 0) {
                LogBox.AppendText(string.Join(Environment.NewLine, batch) + Environment.NewLine);
                if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text[^40000..];
                LogBox.ScrollToEnd();
            }

            if (engine?.IsRecording == true) {
                UpdateRecordUi(true, engine.RecordElapsed);
                previewWindow?.UpdateRecordState(true, engine.RecordElapsed);
            }
        };
        logTimer.Start();
        Closing += OnClosing;
        KeyDown += MainWindow_KeyDown;
        SystemEvents.SessionEnding += (_, _) => { forceExit = true; };
        SourceInitialized += (_, _) => {
            trayIcon = new TrayIcon(this,
                onOpen: () => Dispatcher.Invoke(() => {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                }),
                onToggleStream: () => Dispatcher.Invoke(() => {
                    if (engine?.Running == true) Stop_Click(this, new RoutedEventArgs());
                    else Start_Click(this, new RoutedEventArgs());
                }),
                onExit: () => Dispatcher.Invoke(() => {
                    forceExit = true;
                    trayIcon?.Dispose();
                    trayIcon = null;
                    Close();
                }),
                onToggleMute: () => Dispatcher.Invoke(() => {
                    PrivacyMute_Click(this, new RoutedEventArgs());
                }),
                onToggleHud: () => Dispatcher.Invoke(() => {
                    ToggleHud_Click(this, new RoutedEventArgs());
                }),
                onApplyPreset: (presetName) => Dispatcher.Invoke(() => {
                    ApplyPreset(presetName);
                }),
                onSnapshot: () => Dispatcher.Invoke(TriggerSnapshot),
                onToggleRecord: () => Dispatcher.Invoke(ToggleRecording));
            trayIcon.Initialize();
        };
        StateChanged += (_, _) => {
            UpdatePreviewActivity();
            if (WindowState == WindowState.Minimized && settings.MinimizeToTray) {
                Hide();
            }
        };

        thermalGuard = new ThermalGuard(settings,
            onThrottle: (reducedBitrate, temp, reason) => Dispatcher.Invoke(() => {
                Log(L.Format("s_1fb6a0a12d0b", reason, reducedBitrate));
                trayIcon?.UpdateTip(L.Format("s_3244b557ab19", temp, reducedBitrate));
                if (engine?.Running == true) {
                    var s = Read();
                    s.BitrateMbps = reducedBitrate;
                    _ = engine.UpdateControls(s, CancellationToken.None);
                }
            }),
            onRestore: (origBitrate, temp) => Dispatcher.Invoke(() => {
                Log(L.Format("s_9de2c346447a", temp, origBitrate));
                if (engine?.Running == true) {
                    _ = engine.UpdateControls(Read(), CancellationToken.None);
                }
            }));

        usbWatcherTimer.Tick += async (_, _) => {
            if (safeMode) return;
            if (busy || closing || closed || engine?.Running == true) return;
            if (AutoStartOnUsb.IsChecked != true) return;
            try {
                var devs = await AdbController.ListDevicesAsync(settings.AdbPath, CancellationToken.None);
                var usbDev = devs.FirstOrDefault(d => !d.IsNetwork && d.Authorized);
                if (usbDev != null) {
                    Log(L.Format("s_25c687dc8988", usbDev.Display));
                    await Start();
                }
            } catch { }
        };
        usbWatcherTimer.Start();

        Loaded += async (_, _) => {
            if (Environment.GetCommandLineArgs().Any(a => a is "--tray" or "--minimized")) {
                WindowState = WindowState.Minimized;
                Hide();
            }
            if (safeMode) {
                Log(L.Get("s_96cf73b80ba7"));
            } else if (settings.AutoStart) await Start();
            else await Scan(showErrors: false,allowUsbSwitch:false);
        };
        Log(L.Get("s_b3f65a721581") + Settings.FilePath);
    }

    private void QueueSave() {
        if (filling || busy || settings == null) return;
        if (engine?.Running != true && Fps?.SelectedItem is FpsChoice selected && FpsText != null)
            FpsText.Text = $"— / {selected.Value} fps";
        settings = Read();
        saveTimer.Stop();
        saveTimer.Start();
        if (engine?.Running == true) {
            controlsDebounceTimer.Stop();
            controlsDebounceTimer.Start();
        }
    }

    private static int TransportIndex(string value) => value switch { "wifi" => 1, "usb" => 2, "direct" => 3, _ => 0 };
    private static string TransportValue(int index) => index switch { 1 => "wifi", 2 => "usb", 3 => "direct", _ => "auto" };

    private void Fill() {
        filling = true;
        try {
            Transport.SelectedIndex = TransportIndex(settings.Transport);
            PhoneIp.Text = settings.PhoneIp;
            PcIp.Text = settings.PcIp;
            AutoPcIp.IsChecked = settings.AutoPcIp;
            PcIp.IsEnabled = !settings.AutoPcIp;
            RtpPort.Text = settings.RtpPort.ToString();
            UsbPort.Text = settings.UsbPort.ToString();
            ObsPort.Text = settings.ObsPort.ToString();
            PreviewPort.Text = settings.PreviewPort.ToString();
            Bitrate.Text = settings.BitrateMbps.ToString();
            CodecChoice.SelectedIndex = settings.Codec == "hevc" ? 1 : 0;
            Preview.IsChecked = settings.Preview;
            ObsOutput.IsChecked = settings.Obs;
            VirtualCamera.IsChecked = settings.VirtualCamera;
            LowLatency.IsChecked = settings.LowLatency;
            WifiLimit.SelectedIndex = settings.WifiLimitMbps switch {
                12 => 1, 20 => 2, 32 => 3, _ => 0
            };
            AdaptiveBitrate.IsChecked = settings.AdaptiveBitrate;
            MinimumWifiBitrate.Text = settings.MinimumWifiBitrateMbps.ToString();
            PowerMode.SelectedIndex = settings.PowerMode switch { "maximum" => 0, "saving" => 2, _ => 1 };
            ScreenOff.IsChecked = settings.ScreenOff;
            AutoStart.IsChecked = settings.AutoStart;
            FocusMode.SelectedIndex = Math.Max(0, Array.IndexOf(focusValues, settings.Focus));
            WhiteBalance.SelectedIndex = Math.Max(0, Array.IndexOf(wbValues, settings.Wb));
            FocusDistance.Text = settings.FocusDistance.ToString(CultureInfo.InvariantCulture);
            Exposure.Text = settings.Exposure.ToString();
            Torch.IsChecked = settings.Torch;
            FaceTracking.IsChecked = settings.FaceTracking;
            UpdateFaceTrackingUI(settings.FaceTracking);
            AutoFraming.IsChecked = settings.AutoFraming;
            AutoFramingZoomSlider.Value = settings.AutoFramingZoom;
            AutoFramingSpeedSlider.Value = settings.AutoFramingSpeed;
            AutoFramingDeadzoneSlider.Value = settings.AutoFramingDeadzone;
            UpdateAutoFramingSlidersText();
            UpdateAutoFramingUI(settings.AutoFraming);
            LockAeAwb.IsChecked = settings.LockAeAwb;
            UpdateLockAeAwbUI(settings.LockAeAwb);
            Stabilization.IsChecked = settings.Stabilization;
            if (StabilizationModeChoice != null) StabilizationModeChoice.SelectedIndex = settings.StabilizationMode == "standard" ? 0 : 1;
            UpdateGridUI(settings.CompositionGrid);
            ColorProfileChoice.SelectedIndex = settings.ColorProfile?.ToLowerInvariant() switch {
                "clean" => 1,
                "warm" => 2,
                "teal_orange" => 3,
                "cold" => 4,
                "noir" => 5,
                _ => 0
            };
            BackgroundEffectChoice.SelectedIndex = settings.BackgroundEffect switch {
                "ai_bokeh" => 1,
                "ai_greenscreen" => 2,
                "ai_transparent" => 3,
                "ai_custom" => 4,
                "ai_spotlight" => 5,
                "bokeh" => 6,
                "spotlight" => 7,
                "greenscreen" => 8,
                "studio_dark" => 9,
                _ => 0
            };
            AiSegmentationCheck.IsChecked = settings.AiSegmentation;
            CustomBgPathBox.Text = settings.CustomBackgroundImage ?? "";
            CustomBgPanel.Visibility = (BackgroundEffectChoice.SelectedIndex == 4) ? Visibility.Visible : Visibility.Collapsed;
            AiFeatherSlider.Value = Math.Clamp(settings.AiEdgeFeather, 0.05f, 0.40f);
            AiFeatherText.Text = $"{(int)(AiFeatherSlider.Value * 100)}%";
            SuperResolution4KCheck.IsChecked = settings.SuperResolution4K;
            SuperResolutionSharpnessSlider.Value = Math.Clamp(settings.SuperResolutionSharpness, 0.0f, 0.50f);
            SuperResolutionSharpnessText.Text = $"{(int)(SuperResolutionSharpnessSlider.Value * 100)}%";
            SkinSmoothing.IsChecked = settings.SkinSmoothing;
            BlurStrength.Text = settings.BackgroundBlurStrength.ToString();
            UpdatePeakingUI(settings.FocusPeaking);
            UpdateZebraUI(settings.ZebraPattern);
            ZoomChoice.SelectedIndex = settings.Zoom switch {
                >= 2.8f => 4,
                >= 1.8f => 3,
                >= 1.4f => 2,
                >= 1.15f => 1,
                _ => 0
            };
            ForceSamsungLegacy.IsChecked = settings.ForceSamsungLegacy;
            FlipHorizontal.IsChecked = settings.FlipHorizontal;
            RotationChoice.SelectedIndex = settings.Rotation switch { 90 => 1, 180 => 2, 270 => 3, _ => 0 };
            BrightnessSlider.Value = settings.Brightness;
            ContrastSlider.Value = settings.Contrast;
            SaturationSlider.Value = settings.Saturation;
            UpdateColorSlidersText();
            BatteryProtect.IsChecked = settings.BatteryProtect;
            AutoReconnect.IsChecked = settings.AutoReconnect;
            AutoStartOnUsb.IsChecked = settings.AutoStartOnUsb;
            MinimizeToTray.IsChecked = settings.MinimizeToTray;
            RunOnStartup.IsChecked = settings.RunOnStartup;
            ShutterChoice.SelectedIndex = settings.ShutterSpeedNs switch {
                40_000_000L => 1,
                33_333_333L => 2,
                20_000_000L => 3,
                16_666_667L => 4,
                10_000_000L => 5,
                8_333_333L => 6,
                5_000_000L => 7,
                4_000_000L => 8,
                2_000_000L => 9,
                1_000_000L => 10,
                _ => 0
            };
            IsoChoice.SelectedIndex = settings.ManualIso switch {
                50 => 1,
                100 => 2,
                200 => 3,
                400 => 4,
                800 => 5,
                1600 => 6,
                3200 => 7,
                _ => 0
            };
            WbKelvinChoice.SelectedIndex = settings.ManualWbKelvin switch {
                2800 => 1,
                3200 => 2,
                4000 => 3,
                5000 => 4,
                5600 => 5,
                6500 => 6,
                7500 => 7,
                _ => 0
            };
            ThermalGuardCheck.IsChecked = settings.ThermalGuard;
            ThermalThreshold.Text = settings.ThermalThresholdC.ToString();
            SpoutOutput.IsChecked = settings.SpoutOutput;
            OrientationModeChoice.SelectedIndex = settings.OrientationMode switch {
                "9:16_crop" => 1,
                "9:16_autoframing" => 2,
                "9:16_fit" => 3,
                _ => 0
            };
            if (BasicOrientation169 != null && BasicOrientation916 != null) {
                BasicOrientation169.IsChecked = !settings.OrientationMode.StartsWith("9:16");
                BasicOrientation916.IsChecked = settings.OrientationMode.StartsWith("9:16");
            }
            if (BasicVirtualCamCheck != null) BasicVirtualCamCheck.IsChecked = settings.VirtualCamera;
            if (BasicSpoutCheck != null) BasicSpoutCheck.IsChecked = settings.SpoutOutput;
            if (BasicFaceTrackingCheck != null) BasicFaceTrackingCheck.IsChecked = settings.AutoFraming;
            if (BasicStabilizationCheck != null) BasicStabilizationCheck.IsChecked = settings.Stabilization;
            if (BasicSuperSteadyCheck != null) BasicSuperSteadyCheck.IsChecked = settings.StabilizationMode != "standard";
            UpdateModeUI();
            PrivacyMuteButton.Background = settings.PrivacyMute ? new SolidColorBrush(Color.FromRgb(220, 60, 60)) : null;
            PrivacyMuteButton.Content = settings.PrivacyMute ? "🔒 MUTED" : "🔒 MUTE";
            AdbPath.Text = settings.AdbPath;
            FfmpegPath.Text = settings.FfmpegPath;
            FfplayPath.Text = settings.FfplayPath;
            ObsPath.Text = settings.ObsPath;
            UpdateDevices([], settings.DeviceSerial);
            ApplyCapabilities(capabilities, settings.CameraKey, settings.Width, settings.Height, settings.Fps);
            SelectClosestResolution(settings.Width, settings.Height);
            SelectClosestFps(settings.Fps);
            PopulateCustomPresets();
            faceOverlayEnabled = settings.ShowFaceOverlay;
            UpdateFaceOverlayButtonUI();
            UpdateWbUI();
            if (settings.ShowHud) {
                Dispatcher.BeginInvoke(new Action(() => {
                    ToggleHud_Click(this, new RoutedEventArgs());
                }));
            }
        } finally { filling = false; }
    }

    private void UpdateDevices(IEnumerable<AdbDevice> found, string selectedSerial) {
        var choices = new List<Choice> { new("", L.Get("s_2a4ac51473d9")) };
        choices.AddRange(found.Select(d => new Choice(d.Serial, d.Display)));
        if (!string.IsNullOrWhiteSpace(selectedSerial) && choices.All(x => x.Key != selectedSerial))
            choices.Add(new Choice(selectedSerial, L.Get("s_9c1cb78c9c48") + selectedSerial));
        DeviceChoice.ItemsSource = choices;
        DeviceChoice.SelectedValue = selectedSerial;
        if (DeviceChoice.SelectedIndex < 0) DeviceChoice.SelectedIndex = 0;
    }

    private void ApplyCapabilities(PhoneCapabilities? value, string cameraKey, int width, int height, int fps) {
        capabilities = value;
        var cameras = new List<CameraCapability> {
            new() { Key = "auto", Label = L.Get("s_602ade2abfab") }
        };
        if (value != null) cameras.AddRange(value.Cameras);
        if (cameraKey != "auto" && cameras.All(x => x.Key != cameraKey))
            cameras.Add(new CameraCapability { Key = cameraKey, Label = L.Get("s_fea1da995ac5") + cameraKey });
        CameraChoice.ItemsSource = cameras;
        CameraChoice.SelectedValue = cameraKey;
        if (CameraChoice.SelectedIndex < 0) CameraChoice.SelectedIndex = 0;
        if (BasicCameraChoice != null) {
            BasicCameraChoice.ItemsSource = cameras;
            BasicCameraChoice.SelectedValue = cameraKey;
            if (BasicCameraChoice.SelectedIndex < 0) BasicCameraChoice.SelectedIndex = 0;
        }
        RefreshModes(width, height, fps);
        if (value == null) {
            CameraHint.Text = L.Get("s_3b4ff0e8a258");
        } else {
            CameraHint.Text = L.Format("s_bcb8913a046c", value.Manufacturer, value.Model, value.Sdk, value.Cameras.Count) +
                L.Get("s_686f6c605375");
        }
    }

    private CameraCapability? SelectedCamera() {
        var key = (CameraChoice.SelectedValue as string) ?? "auto";
        if (capabilities == null) return null;
        if (key != "auto") return capabilities.Cameras.FirstOrDefault(x => x.Key == key);
        return capabilities.Cameras.FirstOrDefault(x =>
                x.Facing.Equals("back", StringComparison.OrdinalIgnoreCase) || x.Facing == "Задняя")
            ?? capabilities.Cameras.FirstOrDefault();
    }

    private void RefreshModes(int preferredWidth, int preferredHeight, int preferredFps) {
        var modes = SelectedCamera()?.Modes;
        if (modes == null || modes.Count == 0)
            modes = [new VideoCapability { Width = preferredWidth, Height = preferredHeight, Fps = [30, 60] }];
        ResolutionChoice.ItemsSource = modes;
        ResolutionChoice.SelectedItem = modes.FirstOrDefault(x => x.Width == preferredWidth && x.Height == preferredHeight)
            ?? modes.FirstOrDefault(x => x.Width == 1920 && x.Height == 1080)
            ?? modes[0];
        RefreshFps(preferredFps);
    }

    private void RefreshFps(int preferred) {
        var mode = ResolutionChoice.SelectedItem as VideoCapability;
        var values = mode?.Fps?.Distinct().Order().ToList() ?? [30];
        if (values.Count == 0) values.Add(30);
        var items = values.Select(x => new FpsChoice(x)).ToList();
        Fps.ItemsSource = items;
        Fps.SelectedItem = items.FirstOrDefault(x => x.Value == preferred)
            ?? items.OrderBy(x => Math.Abs(x.Value - preferred)).First();
        if (engine?.Running != true && Fps.SelectedItem is FpsChoice selected)
            FpsText.Text = $"— / {selected.Value} fps";
    }

    private Settings Read() {
        var mode = ResolutionChoice.SelectedItem as VideoCapability;
        var fps = (Fps.SelectedItem as FpsChoice)?.Value ?? settings.Fps;
        return new Settings {
            Language = LanguageChoice.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" },
            Transport = TransportValue(Transport.SelectedIndex),
            PhoneIp = PhoneIp.Text.Trim(),
            PcIp = PcIp.Text.Trim(),
            AutoPcIp = AutoPcIp.IsChecked == true,
            DeviceSerial = (DeviceChoice.SelectedValue as string) ?? "",
            RtpPort = int.TryParse(RtpPort.Text, out var rtp) ? rtp : settings.RtpPort,
            UsbPort = int.TryParse(UsbPort.Text, out var usb) ? usb : settings.UsbPort,
            ObsPort = int.TryParse(ObsPort.Text, out var obs) ? obs : settings.ObsPort,
            PreviewPort = int.TryParse(PreviewPort.Text, out var prev) ? prev : settings.PreviewPort,
            Fps = fps,
            BitrateMbps = int.TryParse(Bitrate.Text, out var br) ? br : settings.BitrateMbps,
            Codec = CodecChoice.SelectedIndex == 1 ? "hevc" : "h264",
            CameraKey = (CameraChoice.SelectedValue as string) ?? "auto",
            Width = mode?.Width ?? settings.Width,
            Height = mode?.Height ?? settings.Height,
            Preview = Preview.IsChecked == true,
            Obs = ObsOutput.IsChecked == true,
            VirtualCamera = VirtualCamera.IsChecked == true,
            LowLatency = LowLatency.IsChecked == true,
            WifiFriendly = WifiLimit.SelectedIndex > 0,
            WifiLimitMbps = WifiLimit.SelectedIndex switch { 1 => 12, 2 => 20, 3 => 32, _ => 0 },
            AdaptiveBitrate = AdaptiveBitrate.IsChecked == true,
            MinimumWifiBitrateMbps = int.TryParse(MinimumWifiBitrate.Text, out var mwb) ? mwb : settings.MinimumWifiBitrateMbps,
            PowerMode = PowerMode.SelectedIndex switch { 0 => "maximum", 2 => "saving", _ => "balanced" },
            ScreenOff = ScreenOff.IsChecked == true,
            AutoStart = AutoStart.IsChecked == true,
            Focus = focusValues[Math.Max(0, FocusMode.SelectedIndex)],
            Wb = wbValues[Math.Max(0, WhiteBalance.SelectedIndex)],
            FocusDistance = float.TryParse(FocusDistance.Text.Replace(',', '.'), CultureInfo.InvariantCulture, out var fd) ? fd : settings.FocusDistance,
            Exposure = int.TryParse(Exposure.Text, out var exp) ? exp : settings.Exposure,
            Torch = Torch.IsChecked == true,
            FaceTracking = FaceTracking.IsChecked == true,
            AutoFraming = AutoFraming.IsChecked == true,
            AutoFramingZoom = (float)Math.Round(AutoFramingZoomSlider.Value, 2),
            AutoFramingSpeed = (float)Math.Round(AutoFramingSpeedSlider.Value, 2),
            AutoFramingDeadzone = (float)Math.Round(AutoFramingDeadzoneSlider.Value, 2),
            LockAeAwb = LockAeAwb.IsChecked == true,
            Stabilization = Stabilization.IsChecked == true,
            StabilizationMode = StabilizationModeChoice?.SelectedIndex == 0 ? "standard" : "strong",
            CompositionGrid = gridEnabled,
            FocusPeaking = focusPeakingEnabled,
            ZebraPattern = zebraEnabled,
            ColorProfile = ColorProfileChoice.SelectedIndex switch {
                1 => "clean",
                2 => "warm",
                3 => "teal_orange",
                4 => "cold",
                5 => "noir",
                _ => "none"
            },
            BackgroundEffect = BackgroundEffectChoice.SelectedIndex switch {
                1 => "ai_bokeh",
                2 => "ai_greenscreen",
                3 => "ai_transparent",
                4 => "ai_custom",
                5 => "ai_spotlight",
                6 => "bokeh",
                7 => "spotlight",
                8 => "greenscreen",
                9 => "studio_dark",
                _ => "none"
            },
            AiSegmentation = AiSegmentationCheck.IsChecked == true,
            CustomBackgroundImage = CustomBgPathBox.Text?.Trim() ?? "",
            AiEdgeFeather = (float)AiFeatherSlider.Value,
            SuperResolution4K = SuperResolution4KCheck.IsChecked == true,
            SuperResolutionSharpness = (float)SuperResolutionSharpnessSlider.Value,
            BackgroundBlurStrength = int.TryParse(BlurStrength.Text, out var bs) ? Math.Clamp(bs, 4, 35) : 15,
            SkinSmoothing = SkinSmoothing.IsChecked == true,
            Zoom = ZoomChoice.SelectedIndex switch {
                1 => 1.2f,
                2 => 1.5f,
                3 => 2.0f,
                4 => 3.0f,
                _ => 1.0f
            },
            ForceSamsungLegacy = ForceSamsungLegacy.IsChecked == true,
            FlipHorizontal = FlipHorizontal.IsChecked == true,
            Rotation = RotationChoice.SelectedIndex switch { 1 => 90, 2 => 180, 3 => 270, _ => 0 },
            Brightness = BrightnessSlider.Value,
            Contrast = ContrastSlider.Value,
            Saturation = SaturationSlider.Value,
            BatteryProtect = BatteryProtect.IsChecked == true,
            AutoReconnect = AutoReconnect.IsChecked == true,
            AutoStartOnUsb = AutoStartOnUsb.IsChecked == true,
            MinimizeToTray = MinimizeToTray.IsChecked == true,
            RunOnStartup = RunOnStartup.IsChecked == true,
            ShutterSpeedNs = ShutterChoice.SelectedIndex switch {
                1 => 40_000_000L,
                2 => 33_333_333L,
                3 => 20_000_000L,
                4 => 16_666_667L,
                5 => 10_000_000L,
                6 => 8_333_333L,
                7 => 5_000_000L,
                8 => 4_000_000L,
                9 => 2_000_000L,
                10 => 1_000_000L,
                _ => 0L
            },
            ManualIso = IsoChoice.SelectedIndex switch {
                1 => 50,
                2 => 100,
                3 => 200,
                4 => 400,
                5 => 800,
                6 => 1600,
                7 => 3200,
                _ => 0
            },
            ManualWbKelvin = WbKelvinChoice.SelectedIndex switch {
                1 => 2800,
                2 => 3200,
                3 => 4000,
                4 => 5000,
                5 => 5600,
                6 => 6500,
                7 => 7500,
                _ => 0
            },
            WbRedGain = settings?.WbRedGain ?? 1.0f,
            WbBlueGain = settings?.WbBlueGain ?? 1.0f,
            WbGreenGain = settings?.WbGreenGain ?? 1.0f,
            ThermalGuard = ThermalGuardCheck.IsChecked == true,
            ThermalThresholdC = int.TryParse(ThermalThreshold.Text, out var tt) ? tt : (settings?.ThermalThresholdC ?? 52),
            SpoutOutput = SpoutOutput.IsChecked == true,
            ShowHud = hudWindow?.IsVisible ?? (settings?.ShowHud ?? false),
            HudLeft = hudWindow?.Left ?? (settings?.HudLeft ?? 40),
            HudTop = hudWindow?.Top ?? (settings?.HudTop ?? 40),
            ShowFaceOverlay = faceOverlayEnabled,
            PrivacyMute = settings?.PrivacyMute ?? false,
            OrientationMode = OrientationModeChoice.SelectedIndex switch {
                1 => "9:16_crop",
                2 => "9:16_autoframing",
                3 => "9:16_fit",
                _ => "16:9"
            },
            ProMode = settings?.ProMode ?? false,
            QualityProfile = settings?.QualityProfile ?? "balanced",
            AdbPath = AdbPath.Text.Trim(),
            FfmpegPath = FfmpegPath.Text.Trim(),
            FfplayPath = FfplayPath.Text.Trim(),
            ObsPath = ObsPath.Text.Trim(),
            CustomPresets = settings?.CustomPresets != null ? new Dictionary<string, Settings>(settings.CustomPresets, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase)
        };
    }

    private void Log(string text) {
        if (logs.Count < 1000) logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {text}");
        var level = (text.Contains("ошибк", StringComparison.OrdinalIgnoreCase) || 
                     text.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                     text.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("exception", StringComparison.OrdinalIgnoreCase)) 
                     ? LogLevel.Error 
                     : (text.Contains("предупрежд", StringComparison.OrdinalIgnoreCase) || text.Contains("warn", StringComparison.OrdinalIgnoreCase))
                     ? LogLevel.Warning
                     : LogLevel.Info;
        App.Logger.Log(text, level);
    }

    private void Buttons() {
        var running = engine?.Running == true;
        StartButton.IsEnabled = !busy && !running;
        ApplyButton.IsEnabled = !busy && running;
        StopButton.IsEnabled = running || busy;
        TestButton.IsEnabled = !busy && !running;
        ScanButton.IsEnabled = !busy && !running;
        SettingsPanel.IsEnabled = !busy;
        if (BasicStartButton != null) {
            BasicStartButton.Content = running ? L.Get("s_b2e4dba597c4") : L.Get("s_9825166bb975");
            BasicStartButton.Background = running ? new SolidColorBrush(Color.FromRgb(180, 50, 50)) : new SolidColorBrush(Color.FromRgb(28, 169, 137));
            BasicStartButton.Foreground = running ? Brushes.White : new SolidColorBrush(Color.FromRgb(4, 28, 22));
            BasicStartButton.IsEnabled = running || !busy;
        }
        if (BasicTransportChoice != null) BasicTransportChoice.IsEnabled = !busy && !running;
        ModeToggleButton.IsEnabled = !busy;
        PrivacyMuteButton.IsEnabled = !busy;
    }

    private void ModeToggle_Click(object sender, RoutedEventArgs e) {
        settings.ProMode = !settings.ProMode;
        UpdateModeUI();
        QueueSave();
    }

    private void LanguageChoice_Changed(object sender, SelectionChangedEventArgs e) {
        if (filling || settings == null) return;
        settings.Language = LanguageChoice.SelectedIndex switch { 1 => "ru", 2 => "en", _ => "auto" };
        var previousFilling = filling;
        filling = true;
        try {
            L.Configure(settings.Language);
            if (Application.Current != null) foreach (Window window in Application.Current.Windows) L.RefreshWindow(window);
            UpdateModeUI();
            Buttons();
            UpdateVirtualCamStatus();
            Read().Save();
        } catch (Exception ex) { Log("Language: " + ex.Message); }
        finally { filling = previousFilling; }
    }

    private void UpdateModeUI() {
        if (settings == null || BasicPanel == null || SettingsPanel == null || ModeToggleButton == null) return;
        BasicVirtualCamCheck.IsChecked = VirtualCamera.IsChecked;
        BasicSpoutCheck.IsChecked = SpoutOutput.IsChecked;
        BasicFaceTrackingCheck.IsChecked = AutoFraming.IsChecked;
        if (settings.ProMode) {
            BasicPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            ModeToggleButton.Content = L.Get("s_f6066218832c");
            ProPresetBar.Visibility = Visibility.Visible;
        } else {
            BasicPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            ModeToggleButton.Content = L.Get("s_f9a245781fea");
            ProPresetBar.Visibility = Visibility.Collapsed;
        }
    }

    private void BasicCameraChoice_Changed(object sender, SelectionChangedEventArgs e) {
        if (filling || BasicCameraChoice?.SelectedValue == null || CameraChoice == null) return;
        CameraChoice.SelectedValue = BasicCameraChoice.SelectedValue;
    }

    private void BasicOrientation_Checked(object sender, RoutedEventArgs e) {
        if (filling || BasicOrientation169 == null || BasicOrientation916 == null || OrientationModeChoice == null) return;
        if (BasicOrientation169.IsChecked == true) {
            OrientationModeChoice.SelectedIndex = 0;
        } else if (BasicOrientation916.IsChecked == true) {
            OrientationModeChoice.SelectedIndex = 1;
        }
        QueueSave();
    }

    private void OrientationModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (filling || OrientationModeChoice == null) return;
        if (BasicOrientation169 != null && BasicOrientation916 != null) {
            BasicOrientation169.IsChecked = OrientationModeChoice.SelectedIndex == 0;
            BasicOrientation916.IsChecked = OrientationModeChoice.SelectedIndex > 0;
        }
        QueueSave();
    }

    private void BasicVirtualCam_Click(object sender, RoutedEventArgs e) {
        if (filling || VirtualCamera == null || BasicVirtualCamCheck == null) return;
        VirtualCamera.IsChecked = BasicVirtualCamCheck.IsChecked;
        VirtualCamera_Click(sender, e);
    }

    private void BasicSpout_Click(object sender, RoutedEventArgs e) {
        if (filling || SpoutOutput == null || BasicSpoutCheck == null) return;
        SpoutOutput.IsChecked = BasicSpoutCheck.IsChecked;
        QueueSave();
    }

    private void BasicFaceTracking_Click(object sender, RoutedEventArgs e) {
        if (filling || AutoFraming == null || FaceTracking == null || BasicFaceTrackingCheck == null) return;
        AutoFraming.IsChecked = BasicFaceTrackingCheck.IsChecked;
        FaceTracking.IsChecked = BasicFaceTrackingCheck.IsChecked;
        QueueSave();
    }

    private void BasicStabilization_Click(object sender, RoutedEventArgs e) {
        if (filling || Stabilization == null || BasicStabilizationCheck == null) return;
        Stabilization.IsChecked = BasicStabilizationCheck.IsChecked;
        QueueSave();
    }

    private void BasicSuperSteady_Click(object sender, RoutedEventArgs e) {
        if (filling || StabilizationModeChoice == null || BasicSuperSteadyCheck == null) return;
        StabilizationModeChoice.SelectedIndex = BasicSuperSteadyCheck.IsChecked == true ? 1 : 0;
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private void StabilizationMode_Changed(object sender, SelectionChangedEventArgs e) {
        if (filling || StabilizationModeChoice == null) return;
        var isStrong = StabilizationModeChoice.SelectedIndex == 1;
        if (BasicSuperSteadyCheck != null && BasicSuperSteadyCheck.IsChecked != isStrong)
            BasicSuperSteadyCheck.IsChecked = isStrong;
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private async void BasicStart_Click(object sender, RoutedEventArgs e) {
        if (engine?.Running == true) Stop_Click(this, new RoutedEventArgs());
        else await Start();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e) {
        try {
            var report = DiagnosticsReport.Generate(Read(), lastStatus, null, engine?.ConnectionManager);
            Clipboard.SetText(report);
            Log(L.Get("s_ce4a2d2ee47a"));
        } catch (Exception ex) { Log(L.Get("s_d5e2478e2b00") + ex.Message); }
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e) {
        try {
            RollingLogger.OpenFolder();
            Log(L.Format("s_72eb109b619c", RollingLogger.LogDir));
        } catch (Exception ex) { Log(L.Get("s_aa41a39815df") + ex.Message); }
    }

    private void CopyLastErrors_Click(object sender, RoutedEventArgs e) {
        try {
            var errors = App.Logger.GetRecentErrors();
            if (errors.Length == 0) {
                Clipboard.SetText(L.Get("s_51087eafa3e1"));
                Log(L.Get("s_f019855b5561"));
            } else {
                Clipboard.SetText(string.Join(Environment.NewLine, errors));
                Log(L.Format("s_b61b1d1b2b72", errors.Length));
            }
        } catch (Exception ex) { Log(L.Get("s_01480d6d823a") + ex.Message); }
    }

    private async Task Scan(bool showErrors = true,bool allowUsbSwitch=true) {
        if (busy || engine?.Running == true) return;
        busy = true;
        Buttons();
        operation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try {
            var next = Read();
            if (next.Transport == "direct") {
                using var direct = new AoaController();
                var result = await direct.ConnectAsync(operation.Token,allowSwitch:allowUsbSwitch);
                Log(result.Status);
                if (!result.Ready) throw new IOException(result.Status);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                PhoneCapabilities? directPhone = null;
                while (directPhone == null) {
                    var message = await direct.ReadAsync(timeout.Token);
                    if (message?.Type == H3HMessageType.Capabilities)
                        directPhone = PhoneCapabilities.ParseJson(message.Value.Text);
                }
                filling = true;
                settings = next;
                ApplyCapabilities(directPhone, settings.CameraKey, settings.Width, settings.Height, settings.Fps);
                settings.Save();
                DeviceHint.Text = L.Format("s_e763f9bf6fd9", directPhone.Manufacturer, directPhone.Model);
                ConnectionLabel.Text = "●  " + directPhone.Model;
                StateText.Text = L.Format("s_ecfb2b6a1513", directPhone.Cameras.Count);
                return;
            }
            var controller = new AdbController(next, Log);
            await controller.Connect(operation.Token);
            var phone = await controller.GetCapabilities(operation.Token);
            filling = true;
            settings = next;
            settings.DeviceSerial = controller.Serial;
            UpdateDevices(controller.Devices, controller.Serial);
            ApplyCapabilities(phone, settings.CameraKey, settings.Width, settings.Height, settings.Fps);
            settings.Save();
            DeviceHint.Text = L.Format("s_5ad16b250cba", controller.Device?.Display ?? controller.Serial, controller.EffectiveTransport.ToUpperInvariant());
            ConnectionLabel.Text = "●  " + (controller.Device?.Model.Replace('_', ' ') ?? controller.Serial);
            StateText.Text = L.Format("s_501a0c9993b4", phone.Cameras.Count);
        } catch (OperationCanceledException) {
            if (showErrors) Log(L.Get("s_c1a883622f70"));
        } catch (Exception ex) {
            Log(L.Get("s_da6a0dca6635") + ex.Message);
            DeviceHint.Text = ex.Message;
            if (showErrors) StateText.Text = ex.Message;
        } finally {
            filling = false;
            busy = false;
            Buttons();
        }
    }

    private async void Usb60_Click(object sender, RoutedEventArgs e) {
        if (busy || engine?.Running == true) { Log(L.Get("s_ee8515ed216a")); return; }
        Transport.SelectedIndex = 2;
        await Scan(showErrors: true);
        var mode = SelectedCamera()?.Modes.Where(x => x.Fps.Contains(60))
            .OrderByDescending(x => (long)x.Width * x.Height).FirstOrDefault();
        if (mode == null) { Log(L.Get("s_398c43dff846")); return; }
        ResolutionChoice.SelectedItem = mode;
        RefreshFps(60);
        LowLatency.IsChecked = true;
        ScreenOff.IsChecked = true;
        Bitrate.Text = "32";
        Read().Save();
        StateText.Text = L.Format("s_568c00585c0d", mode.Width, mode.Height);
    }

    private async Task Start(bool restart = false) {
        if (busy || (!restart && engine?.Running == true)) return;
        busy = true;
        Buttons();
        saveTimer.Stop();
        operation?.Cancel();
        operation = new();
        try {
            var next = Read();
            if (engine != null) {
                await engine.DisposeAsync();
                engine = null;
            }
            settings = next;
            settings.Save();
            engine = new ReceiverEngine(settings, Log);
            UpdatePreviewActivity();
            engine.Status += status => Dispatcher.BeginInvoke(() => ShowStatus(status));
            engine.PreviewFrame += (buf, w, h) => {
                var snapshotRequest = Volatile.Read(ref requestedSnapshotFrame);
                if (!previewRenderingVisible && snapshotRequest == null) return;
                int needed = w * h * 4;
                lock (latestRawPreviewLock) {
                    if (latestRawPreviewFrame == null || latestRawPreviewFrame.Length != needed) {
                        latestRawPreviewFrame = new byte[needed];
                    }
                    Buffer.BlockCopy(buf, 0, latestRawPreviewFrame, 0, needed);
                    latestRawPreviewW = w;
                    latestRawPreviewH = h;
                }
                snapshotRequest?.TrySetResult();
                if (!previewRenderingVisible) return;
                if (Interlocked.CompareExchange(ref renderingFrame, 1, 0) == 0) {
                    Task.Run(() => {
                        try {
                            if (closing || closed) return;
                            var s = Volatile.Read(ref settings);
                            byte[] toDraw;
                            lock (latestRawPreviewLock) {
                                if (rawRenderBuffer == null || rawRenderBuffer.Length != needed) {
                                    rawRenderBuffer = new byte[needed];
                                }
                                Buffer.BlockCopy(latestRawPreviewFrame!, 0, rawRenderBuffer, 0, needed);
                                toDraw = rawRenderBuffer;
                            }

                            lock (previewProcessLock) {
                                bool hasColor = StudioEffectsProcessor.HasColorAdjustment(s.ColorProfile, s.Brightness, s.Contrast, s.Saturation, s.WbRedGain, s.WbGreenGain, s.WbBlueGain);
                                if (s.BackgroundEffect != "none" || s.SkinSmoothing || hasColor) {
                                    if (processedPreviewBuffer == null || processedPreviewBuffer.Length != needed) {
                                        processedPreviewBuffer = new byte[needed];
                                    }
                                    StudioEffectsProcessor.ApplyEffects(toDraw, processedPreviewBuffer, w, h,
                                        s.BackgroundEffect, s.BackgroundBlurStrength, s.SkinSmoothing,
                                        focusNormX: 0.5f, focusNormY: 0.45f,
                                        s.ColorProfile, s.Brightness, s.Contrast, s.Saturation,
                                        s.CustomBackgroundImage, s.AiSegmentation, s.AiEdgeFeather,
                                        s.WbRedGain, s.WbGreenGain, s.WbBlueGain);
                                    toDraw = processedPreviewBuffer;
                                }

                                if (focusPeakingEnabled || zebraEnabled) {
                                    if (overlayBuffer == null || overlayBuffer.Length != needed) {
                                        overlayBuffer = new byte[needed];
                                    }
                                    VideoOverlayProcessor.ProcessFrame(toDraw, overlayBuffer, w, h, focusPeakingEnabled, zebraEnabled);
                                    toDraw = overlayBuffer;
                                }

                                if (previewUiBuffers[0] == null || previewUiBuffers[0].Length != needed) {
                                    previewUiBuffers[0] = new byte[needed];
                                    previewUiBuffers[1] = new byte[needed];
                                }
                                var targetBuf = previewUiBuffers[previewUiBufIndex];
                                previewUiBufIndex = 1 - previewUiBufIndex;
                                Buffer.BlockCopy(toDraw, 0, targetBuf, 0, needed);
                                toDraw = targetBuf;
                            }

                            Dispatcher.BeginInvoke(DispatcherPriority.Render, () => {
                                try {
                                    if (closing || closed) return;
                                    if (previewBitmap == null || previewBitmap.PixelWidth != w || previewBitmap.PixelHeight != h) {
                                        previewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                                        LivePreviewImage.Source = previewBitmap;
                                        PreviewPlaceholder.Visibility = Visibility.Collapsed;
                                        ResolutionBadgeBorder.Visibility = Visibility.Visible;
                                        UpdatePreviewResolutionBadge();
                                        if (gridEnabled) RedrawGrid();
                                    }
                                    previewBitmap.WritePixels(new Int32Rect(0, 0, w, h), toDraw, w * 4, 0);
                                    previewWindow?.RenderFrame(toDraw, w, h);
                                } catch { }
                                finally {
                                    Interlocked.Exchange(ref renderingFrame, 0);
                                }
                            });
                        } catch {
                            Interlocked.Exchange(ref renderingFrame, 0);
                        }
                    });
                }
            };
            engine.PreviewChanged += enabled => Dispatcher.BeginInvoke(() => {
                filling = true;
                try { Preview.IsChecked = enabled; } finally { filling = false; }
                QueueSave();
            });
            await engine.Start(operation.Token);
            UpdateVirtualCamStatus();
            filling = true;
            UpdateDevices([], settings.DeviceSerial);
            filling = false;
            ConnectionLabel.Text = "●  Android connected";
        } catch (OperationCanceledException) {
            Log(L.Get("s_b0717c829207"));
        } catch (Exception ex) {
            Log("START: " + ex.Message);
            StateText.Text = ex.Message;
            ConnectionLabel.Text = L.Get("s_695dfa7ab99f");
            if (engine != null) await engine.DisposeAsync();
            engine = null;
        } finally {
            busy = false;
            Buttons();
        }
    }

    private void Preview_Changed(object sender, RoutedEventArgs e) {
        if (filling || busy || engine?.Running != true) return;
        try { engine.SetPreviewEnabled(Preview.IsChecked == true); }
        catch (Exception ex) {
            Log(ex.Message);
            filling = true;
            try { Preview.IsChecked = engine.PreviewEnabled; } finally { filling = false; }
        }
    }

    private void PowerMode_Changed(object sender, SelectionChangedEventArgs e) {
        if (filling || PowerMode.SelectedIndex < 0) return;
        if (PowerMode.SelectedIndex == 2) {
            var choices = Fps.Items.OfType<FpsChoice>().ToList();
            Fps.SelectedItem = choices.FirstOrDefault(x => x.Value == 30) ?? choices.FirstOrDefault();
            Bitrate.Text = "12";
            PowerModeHint.Text = L.Get("s_3a11bc861ccc");
            Log(L.Get("s_25cf11b085b2"));
        } else if (PowerMode.SelectedIndex == 0) {
            PowerModeHint.Text = L.Get("s_832a31233ff4");
        } else PowerModeHint.Text = L.Get("s_0dc17b2fea47");

        QueueSave();
        if (engine?.Running == true) {
            controlsDebounceTimer.Stop();
            var current = Read();
            thermalGuard?.UpdateSettings(current);
            _ = ApplyControlsSafely(current);
        }
    }

    private async Task ApplyControlsSafely(Settings current) {
        var active = engine;
        if (active == null || busy || closing || closed) return;
        try {
            if (active.RequiresRestart(current)) await Start(restart: true);
            else await active.UpdateControls(current, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(L.Get("s_516219d62901") + ex.Message); }
    }

    private void ShowStatus(LiveStatus s) {
        StateText.Text = L.PhoneText(L.Relocalize(s.State));
        RateText.Text = $"{s.ReceivedMbps:F2} Mbps";
        FpsText.Text = $"{s.DetectedFps:F1} / {s.RequestedFps}";
        ConnectionLabel.Text = "●  " + s.Serial;
        var lossStr = s.LostPackets > 0 ? $"  ·  loss {s.LostPackets}" : "";
        var recStr = s.RecoveredPackets > 0 ? $" (rec {s.RecoveredPackets})" : "";
        var dropStr = s.DroppedFrames > 0 ? $"  ·  drop {s.DroppedFrames}" : "";
        var latStr = s.LatencyMs > 0 ? $"  ·  RTP jitter {s.LatencyMs:F0}ms" : "";
        TechnicalText.Text = $"{s.Transport}  ·  {s.Resolution}{latStr}\nEncoder {s.EncodedMbps:F2} Mbps  ·  {s.Packets:N0} pkts{lossStr}{recStr}{dropStr}\nFFmpeg {s.Ffmpeg}  ·  {s.Elapsed:hh\\:mm\\:ss}\n{s.Thermal}";
        TechnicalText.Text = L.PhoneText(TechnicalText.Text);
        DetailText.Text = L.PhoneText(s.Details);
        lastStatus = s;
        lastPower = s.Power;
        powerHistory.Add(s.Power);
        PowerSourceText.Text = L.Get("s_f30d1d487948") + s.Power.Source;
        PowerHeadlineText.Text = s.Power.Headline;
        var cpuStr = (s.Power.CpuTemperatureC != null || s.Power.CpuPercent != null)
            ? $"CPU {PowerValue(s.Power.CpuTemperatureC, "0.0", "°C")} ({PowerValue(s.Power.CpuPercent, "0.0", "%")})  ·  "
            : "";
        PowerQuickText.Text = $"{cpuStr}Voltage {PowerValue(s.Power.VoltageV, "0.000", "V")}  ·  " +
            $"Current {PowerSigned(s.Power.CurrentMa, "mA")}  ·  Power {PowerSigned(s.Power.PowerW, "W")}  ·  " +
            $"Batt {PowerValue(s.Power.TemperatureC, "0.0", "°C")}";
        PowerDetailsText.Text = s.Power.Details;
        PowerWarningText.Text = powerHistory.Assessment(s.Power);
        DrawPowerChart();
        thermalGuard?.UpdateTelemetry(s.Power.CpuTemperatureC, s.Power.TemperatureC);
        trayIcon?.UpdateTip($"H3H Cam · {s.State} · {s.DetectedFps:F0} FPS · {s.ReceivedMbps:F1} Mbps");
        if (BasicDeviceNameText != null) {
            BasicDeviceNameText.Text = string.IsNullOrWhiteSpace(s.Serial) ? L.Get("s_105fe2f786c3") : s.Serial;
            BasicTransportBadge.Text = s.Transport.ToUpperInvariant();
            BasicBatteryText.Text = $"🔋 {s.Power.Headline}  ·  {(s.Power.TemperatureC.HasValue ? s.Power.TemperatureC.Value.ToString("F1") + " °C" : "")}";
            BasicTransportText.Text = $"{s.Resolution} @ {s.DetectedFps:F0} FPS  ·  {s.ReceivedMbps:F1} Mbps  ·  {s.Thermal}";
        }
        if (hudWindow?.IsVisible == true) {
            hudWindow.UpdateMetrics(s, settings);
        }
        if (faceOverlayEnabled && FaceCanvas != null) {
            UpdateFaceOverlay(s.Face);
        }
    }

    private static string PowerValue(double? value, string format, string unit) => value is { } number
        ? number.ToString(format, CultureInfo.InvariantCulture) + " " + unit : "N/A";
    private static string PowerSigned(double? value, string unit) => value is { } number
        ? number.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " " + unit : "N/A";

    private void PowerChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPowerChart();
    private void DrawPowerChart() {
        var samples = powerHistory.Samples;
        var width = PowerChart.ActualWidth;
        var height = PowerChart.ActualHeight;
        if (width < 20 || height < 10 || samples.Count < 2) return;
        PowerChart.Children.Clear();
        DrawSeries(samples.Select(x => x.Value.Percent is { } v ? (double?)v : null), Color.FromRgb(112,229,195));
        DrawSeries(samples.Select(x => x.Value.TemperatureC), Color.FromRgb(245,112,112));
        DrawSeries(samples.Select(x => x.Value.CurrentMa), Color.FromRgb(89,190,255));
        DrawSeries(samples.Select(x => x.Value.PowerW), Color.FromRgb(230,184,92));

        void DrawSeries(IEnumerable<double?> input, Color color) {
            var values = input.ToArray();
            var valid = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            if (valid.Length < 2) return;
            var min = valid.Min(); var max = valid.Max();
            if (Math.Abs(max - min) < 0.001) { min -= 1; max += 1; }
            var line = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, Opacity = 0.9 };
            for (var i = 0; i < values.Length; i++) if (values[i] is { } value)
                line.Points.Add(new Point(i * width / Math.Max(1, values.Length - 1), height - 3 - (value - min) / (max - min) * (height - 6)));
            PowerChart.Children.Add(line);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Scan();
    private async void Start_Click(object sender, RoutedEventArgs e) => await Start();
    private async void Apply_Click(object sender, RoutedEventArgs e) {
        if (busy) return;
        if (engine?.Running == true) {
            await Start(restart: true);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) {
        operation?.Cancel();
        busy = true;
        Buttons();
        try {
            if (engine != null) await engine.Stop();
            StateText.Text = L.Get("s_3028fa0907c0");
            ConnectionLabel.Text = L.Get("s_b37981243396");
            if (BasicDeviceNameText != null) {
                BasicDeviceNameText.Text = L.Get("s_07a71c8952df");
                BasicTransportBadge.Text = L.Get("s_c04cb19740df");
                BasicTransportText.Text = L.Get("s_befdd34ad631");
            }
            previewBitmap = null;
            LivePreviewImage.Source = null;
            UpdateRecordUi(false, TimeSpan.Zero);
            previewWindow?.UpdateRecordState(false, TimeSpan.Zero);
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ResolutionBadgeBorder.Visibility = Visibility.Collapsed;
            FocusCanvas.Children.Clear();
            GridCanvas.Children.Clear();
        } catch (Exception ex) { Log(ex.Message); }
        finally { busy = false; Buttons(); }
    }

    private async void Test_Click(object sender, RoutedEventArgs e) {
        if (busy || engine?.Running == true) return;
        busy = true;
        Buttons();
        operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            settings = Read();
            await using var test = new ReceiverEngine(settings, Log);
            await test.Test(operation.Token);
            if (settings.Transport == "direct") {
                settings.Save();
                ConnectionLabel.Text = L.Get("s_0c48f4ab1e5b");
                StateText.Text = L.Get("s_44e33ad39a57");
                return;
            }
            var controller = new AdbController(settings, Log);
            await controller.Connect(operation.Token);
            var phone = await controller.GetCapabilities(operation.Token);
            filling = true;
            settings.DeviceSerial = controller.Serial;
            UpdateDevices(controller.Devices, controller.Serial);
            ApplyCapabilities(phone, settings.CameraKey, settings.Width, settings.Height, settings.Fps);
            filling = false;
            settings.Save();
            ConnectionLabel.Text = L.Get("s_0c48f4ab1e5b");
            StateText.Text = L.Get("s_2ea6e62d5e86");
        } catch (Exception ex) {
            Log(L.Get("s_703452b3f462") + ex.Message);
            StateText.Text = ex.Message;
            ConnectionLabel.Text = L.Get("s_4913b0c30b5d");
        } finally {
            filling = false;
            busy = false;
            Buttons();
        }
    }

    private void CameraChoice_Changed(object sender, SelectionChangedEventArgs e) {
        if (!filling && IsInitialized) {
            RefreshModes(settings.Width, settings.Height, settings.Fps);
            if (engine?.Running == true) {
                var current = Read();
                _ = engine.UpdateControls(current, CancellationToken.None);
            }
        }
        QueueSave();
    }

    private void LensWide_Click(object sender, RoutedEventArgs e) => SwitchLens("wide", 0.6f);
    private void LensMain_Click(object sender, RoutedEventArgs e) => SwitchLens("main", 1.0f);
    private void LensTele_Click(object sender, RoutedEventArgs e) => SwitchLens("tele", 2.0f);

    private void SwitchLens(string type, float zoomTarget) {
        CameraCapability? target = null;
        if (capabilities != null && capabilities.Cameras.Count > 0) {
            if (type == "wide") {
                target = capabilities.Cameras.FirstOrDefault(c =>
                    c.Label.Contains("wide", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("0.5", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("0.6", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("14", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("15", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("16", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.Equals("2", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.EndsWith("/2", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.Equals("camera2", StringComparison.OrdinalIgnoreCase));
            } else if (type == "tele") {
                target = capabilities.Cameras.FirstOrDefault(c =>
                    c.Label.Contains("tele", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("2x", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("3x", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("70", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("85", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("100", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.Equals("3", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.EndsWith("/3", StringComparison.OrdinalIgnoreCase) ||
                    c.Key.Equals("camera3", StringComparison.OrdinalIgnoreCase));
            } else {
                target = capabilities.Cameras.FirstOrDefault(c =>
                    c.Facing.Equals("back", StringComparison.OrdinalIgnoreCase) &&
                    !c.Label.Contains("wide", StringComparison.OrdinalIgnoreCase) &&
                    !c.Label.Contains("tele", StringComparison.OrdinalIgnoreCase))
                    ?? capabilities.Cameras.FirstOrDefault();
            }
        }

        if (target != null) {
            CameraChoice.SelectedValue = target.Key;
            Log(L.Format("s_8002c28153b7", target.Label));
            if (engine?.Running == true) {
                var current = Read();
                _ = engine.UpdateControls(current, CancellationToken.None);
            }
        } else {
            ZoomChoice.SelectedIndex = zoomTarget switch {
                >= 2.8f => 4,
                >= 1.8f => 3,
                >= 1.4f => 2,
                >= 1.15f => 1,
                _ => 0
            };
            settings.Zoom = zoomTarget;
            Log(capabilities == null 
                ? L.Format("s_9ca724f4b372", zoomTarget)
                : L.Format("s_9415ffcc9a10", type, zoomTarget));
            QueueSave();
            if (engine?.Running == true) {
                var current = Read();
                current.Zoom = zoomTarget;
                _ = engine.UpdateControls(current, CancellationToken.None);
            }
        }
    }

    private void ResolutionChoice_Changed(object sender, SelectionChangedEventArgs e) {
        if (!filling && IsInitialized) RefreshFps(settings.Fps);
        QueueSave();
    }

    private void AutoPcIp_Changed(object sender, RoutedEventArgs e) {
        if (PcIp != null) PcIp.IsEnabled = AutoPcIp.IsChecked != true;
        QueueSave();
    }

    private void UpdateWbUI() {
        if (WbStatusBadge == null) return;
        if (settings.ManualWbKelvin > 0) {
            WbStatusBadge.Text = $"{settings.ManualWbKelvin} K";
            WbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0xD8, 0xB2));
            if (WbCalibrationDetails != null) {
                WbCalibrationDetails.Text = L.Format("s_8f0a1929105e", settings.ManualWbKelvin, settings.WbRedGain, settings.WbBlueGain);
                WbCalibrationDetails.Visibility = Visibility.Visible;
            }
        } else {
            WbStatusBadge.Text = "AWB Auto";
            WbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x84, 0x98, 0xB4));
            if (WbCalibrationDetails != null) WbCalibrationDetails.Visibility = Visibility.Collapsed;
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F9 || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)) {
            TriggerSnapshot();
            e.Handled = true;
        } else if (e.Key == Key.F10 || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.R)) {
            ToggleRecording();
            e.Handled = true;
        }
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e) => TriggerSnapshot();

    private void Record_Click(object sender, RoutedEventArgs e) => ToggleRecording();

    public async void TriggerSnapshot() {
        // A tray snapshot briefly wakes preview sampling and waits for a fresh frame.
        // Do not save the last frame rendered before the window was hidden.
        if (!previewRenderingVisible && engine?.Running == true && engine.PreviewEnabled && !settings.PrivacyMute) {
            if (requestedSnapshotFrame != null) return;
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref requestedSnapshotFrame, request);
            engine.SetPreviewActivity(true);
            try { await request.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { Log(L.Get("s_9274203cc23e")); return; }
            finally { Volatile.Write(ref requestedSnapshotFrame, null); UpdatePreviewActivity(); }
        }
        byte[]? snapFrame = null;
        int frameW = 0, frameH = 0;
        lock (latestRawPreviewLock) {
            if (latestRawPreviewFrame != null && latestRawPreviewW > 0 && latestRawPreviewH > 0) {
                snapFrame = new byte[latestRawPreviewFrame.Length];
                Buffer.BlockCopy(latestRawPreviewFrame, 0, snapFrame, 0, latestRawPreviewFrame.Length);
                frameW = latestRawPreviewW;
                frameH = latestRawPreviewH;
            }
        }

        if (snapFrame == null || frameW <= 0 || frameH <= 0) {
            Log(L.Get("s_fcb8cd6097ad"));
            return;
        }

        TriggerCameraFlash();
        previewWindow?.TriggerFlash();

        _ = Task.Run(() => {
            try {
                var (filePath, w, h, size) = SnapshotManager.TakeSnapshot(
                    snapFrame, frameW, frameH, upscale4K: settings.SuperResolution4K);
                double mb = size / (1024.0 * 1024.0);
                Dispatcher.Invoke(() => {
                    Log(L.Format("s_da08d95373ce", Path.GetFileName(filePath), w, h, mb));
                    trayIcon?.ShowBalloon(L.Get("s_6aaadcb873c3"), L.Format("s_4de6c38fc54f", Path.GetFileName(filePath), w, h, mb));
                });
            } catch (Exception ex) {
                Dispatcher.Invoke(() => Log(L.Get("s_6b2995c96609") + ex.Message));
            }
        });
    }

    private void TriggerCameraFlash() {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(250));
        CameraFlashOverlay?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public void ToggleRecording() {
        if (engine == null || !engine.Running) {
            Log(L.Get("s_363281ec9f64"));
            return;
        }

        if (engine.IsRecording) {
            var (path, duration, size) = engine.StopRecording();
            UpdateRecordUi(false, TimeSpan.Zero);
            previewWindow?.UpdateRecordState(false, TimeSpan.Zero);
            double mb = size / (1024.0 * 1024.0);
            Log(L.Format("s_59712ad503b8", Path.GetFileName(path), mb, duration));
            trayIcon?.ShowBalloon(L.Get("s_473ecd2ec400"), L.Format("s_0861a28945d3", Path.GetFileName(path), mb, duration));
        } else {
            try {
                var path = engine.StartRecording();
                UpdateRecordUi(true, TimeSpan.Zero);
                previewWindow?.UpdateRecordState(true, TimeSpan.Zero);
                Log(L.Format("s_cf8d38e5cdd7", Path.GetFileName(path)));
            } catch (Exception ex) {
                Log(L.Get("s_324f16954d36") + ex.Message);
            }
        }
    }

    private void UpdateRecordUi(bool isRecording, TimeSpan elapsed) {
        if (RecBadgeBorder != null)
            RecBadgeBorder.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        if (RecBadgeText != null && isRecording)
            RecBadgeText.Text = $"🔴 REC {elapsed:mm\\:ss}";
        if (RecordButton != null) {
            RecordButton.Content = isRecording ? L.Format("s_4d566ce84b70", elapsed) : L.Get("s_70c75c89eb79");
            RecordButton.Background = isRecording
                ? new SolidColorBrush(Color.FromRgb(90, 18, 26))
                : new SolidColorBrush(Color.FromRgb(42, 20, 26));
            RecordButton.Foreground = isRecording
                ? new SolidColorBrush(Color.FromRgb(255, 77, 109))
                : new SolidColorBrush(Color.FromRgb(255, 107, 139));
        }
    }

    private void CalibrateWb_Click(object sender, RoutedEventArgs e) {
        if (engine?.Running != true) {
            Log(L.Get("s_d703aaece641"));
            MessageBox.Show(
                L.Get("s_7106ba26fb3b"),
                L.Get("s_057ad5490418"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        byte[]? sampleFrame = null;
        int frameW = 0, frameH = 0;
        lock (latestRawPreviewLock) {
            if (latestRawPreviewFrame != null && latestRawPreviewW > 0 && latestRawPreviewH > 0) {
                sampleFrame = new byte[latestRawPreviewFrame.Length];
                Buffer.BlockCopy(latestRawPreviewFrame, 0, sampleFrame, 0, latestRawPreviewFrame.Length);
                frameW = latestRawPreviewW;
                frameH = latestRawPreviewH;
            }
        }

        if (sampleFrame == null || frameW <= 0 || frameH <= 0) {
            Log(L.Get("s_5ed05f637ddb"));
            return;
        }

        // Show visual calibration target box on preview
        if (WbTargetBox != null) {
            WbTargetBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xB2));
            if (WbTargetBoxText != null) {
                WbTargetBoxText.Text = L.Get("s_2b612c03931a");
                WbTargetBoxText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xB2));
            }
            WbTargetBox.Visibility = Visibility.Visible;
            wbOverlayTimer.Stop();
            wbOverlayTimer.Start();
        }

        var res = WhitebalanceCalibrator.CalibrateFromBgra(sampleFrame, frameW, frameH, centerRatio: 0.30);
        if (!res.Success) {
            Log(res.Message);
            if (WbTargetBox != null && WbTargetBoxText != null) {
                WbTargetBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
                WbTargetBoxText.Text = L.Get("s_699eccfb8999");
                WbTargetBoxText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
            }
            if (WbCalibrationDetails != null) {
                WbCalibrationDetails.Text = res.Message;
                WbCalibrationDetails.Visibility = Visibility.Visible;
            }
            return;
        }

        // Apply calibrated settings
        settings.ManualWbKelvin = res.NearestPresetKelvin;
        settings.WbRedGain = res.RedGain;
        settings.WbBlueGain = res.BlueGain;
        settings.WbGreenGain = res.GreenGain;
        settings.LockAeAwb = true;

        // Update UI
        filling = true;
        try {
            WbKelvinChoice.SelectedIndex = res.NearestPresetKelvin switch {
                2800 => 1,
                3200 => 2,
                4000 => 3,
                5000 => 4,
                5600 => 5,
                6500 => 6,
                7500 => 7,
                _ => 0
            };
            LockAeAwb.IsChecked = true;
            UpdateLockAeAwbUI(settings.LockAeAwb);
            UpdateWbUI();
        } finally {
            filling = false;
        }

        if (WbTargetBox != null && WbTargetBoxText != null) {
            WbTargetBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x42, 0xD8, 0xB2));
            WbTargetBoxText.Text = L.Format("s_007f45a3f85b", res.EstimatedKelvin);
            WbTargetBoxText.Foreground = new SolidColorBrush(Color.FromRgb(0x42, 0xD8, 0xB2));
        }

        Log(res.Message);
        QueueSave();

        // Push controls dynamically to phone camera
        if (engine?.Running == true) {
            controlsDebounceTimer.Stop();
            var current = Read();
            _ = engine.UpdateControls(current, CancellationToken.None);
        }
    }

    private void ResetWb_Click(object sender, RoutedEventArgs e) {
        settings.ManualWbKelvin = 0;
        settings.WbRedGain = 1.0f;
        settings.WbBlueGain = 1.0f;
        settings.WbGreenGain = 1.0f;
        settings.LockAeAwb = false;

        filling = true;
        try {
            WbKelvinChoice.SelectedIndex = 0;
            WhiteBalance.SelectedIndex = 0;
            LockAeAwb.IsChecked = false;
            UpdateLockAeAwbUI(settings.LockAeAwb);
            UpdateWbUI();
            if (WbTargetBox != null) WbTargetBox.Visibility = Visibility.Collapsed;
        } finally {
            filling = false;
        }

        Log(L.Get("s_0784d669989a"));
        QueueSave();

        if (engine?.Running == true) {
            controlsDebounceTimer.Stop();
            var current = Read();
            _ = engine.UpdateControls(current, CancellationToken.None);
        }
    }

    private void UpdateVirtualCamStatus() {
        var status = VirtualCameraDriver.GetStatus();
        VirtualCamStatusText.Text = status.Ready
            ? L.Format("s_06cd7525cdfb", status.Message)
            : L.Format("s_e3a8076de070", status.Message);
        VirtualCamStatusText.Foreground = new SolidColorBrush(status.Ready
            ? Color.FromRgb(112, 229, 195) : Color.FromRgb(245, 184, 92));
        InstallVirtualCamButton.Content = L.Get("s_559aaeb2dac7");
        InstallVirtualCamButton.Visibility = status.Ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task<bool>? virtualCamInstall;
    private async Task<bool> EnsureVirtualCamAsync() {
        InstallVirtualCamButton.IsEnabled = false;
        try {
            virtualCamInstall ??= VirtualCameraDriver.InstallAsync();
            bool success = await virtualCamInstall;
            if (!success) throw new IOException(L.Get("s_081b3a1e7eb2"));
            Log(L.Format("s_36790668248c", VirtualCameraDriver.GetStatus().DeviceName));
            return true;
        } catch (Exception ex) {
            Log(L.Get("s_57b94641fcd3") + ex.Message);
            MessageBox.Show(ex.Message, L.Get("s_86565540c018"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        } finally {
            virtualCamInstall = null;
            UpdateVirtualCamStatus();
            InstallVirtualCamButton.IsEnabled = true;
        }
    }

    private async void VirtualCamera_Click(object sender, RoutedEventArgs e) {
        if (filling) return;
        if (BasicVirtualCamCheck != null) BasicVirtualCamCheck.IsChecked = VirtualCamera.IsChecked;
        controlsDebounceTimer.Stop();
        if (VirtualCamera.IsChecked == true) await EnsureVirtualCamAsync();
        QueueSave();
    }

    private async void InstallVirtualCam_Click(object sender, RoutedEventArgs e) {
        if (await EnsureVirtualCamAsync() && engine?.Running == true && VirtualCamera.IsChecked == true)
            await Start(restart: true);
    }

    private void ShowQr_Click(object sender, RoutedEventArgs e) {
        try {
            var current = Read();
            var pcIp = current.PcIp;
            if (string.IsNullOrWhiteSpace(pcIp) || pcIp == "127.0.0.1") {
                pcIp = AdbController.FindLocalIpv4();
            }
            var connectUri = $"h3hcam://wifi?pc={pcIp}&port={current.RtpPort}&fps={current.Fps}&codec={current.Codec}";
            var qrBmp = QrCodeGenerator.GenerateBitmap(connectUri, scale: 9, border: 4);

            var qrWin = new Window {
                Title = L.Get("s_b4ae5af1d571"),
                Width = 420,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(12, 20, 33)),
                ResizeMode = ResizeMode.NoResize
            };
            var root = new StackPanel { Margin = new Thickness(24) };
            root.Children.Add(new TextBlock {
                Text = L.Get("s_4c14642081ee"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock {
                Text = L.Format("s_4113ce7323a2", pcIp, current.RtpPort),
                Foreground = new SolidColorBrush(Color.FromRgb(132, 152, 180)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            });
            var img = new Image {
                Source = qrBmp,
                Width = 260,
                Height = 260,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            root.Children.Add(img);

            var statusBlock = new TextBlock {
                Text = L.Get("s_d975b52d3ec7"),
                Foreground = new SolidColorBrush(Color.FromRgb(112, 229, 195)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            root.Children.Add(statusBlock);
            qrWin.Content = root;
            qrWin.ShowDialog();
        } catch (Exception ex) { Log("QR Wi-Fi: " + ex.Message); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void Browse_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFileDialog { Filter = L.Get("s_684ffe9059b6") };
        if (dialog.ShowDialog() == true && sender is Button button &&
            FindName((string)button.Tag) is TextBox box) box.Text = dialog.FileName;
    }

    private void Obs_Click(object sender, RoutedEventArgs e) {
        try {
            var path = ToolPaths.Find("obs64.exe", ObsPath.Text.Trim());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
            if (VirtualCamera.IsChecked == true)
                Log(L.Format("s_8ff2b3893038", VirtualCameraDriver.GetStatus().DeviceName));
            else Log($"OBS: Media Source → Local file off → udp://127.0.0.1:{ObsPort.Text}?fifo_size=4096&overrun_nonfatal=1");
        } catch (Exception ex) { Log(ex.Message); }
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e) {
        try {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var outDir = Path.Combine(Settings.DataDir, "diagnostics");
            Directory.CreateDirectory(outDir);
            var zipPath = Path.Combine(outDir, $"H3H-Cam-Diagnostics-{timestamp}.zip");

            using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create)) {
                var current = Read();
                var sanitized = JsonSerializer.Serialize(new {
                    current.Transport,
                    PhoneIp = MaskIp(current.PhoneIp),
                    PcIp = MaskIp(current.PcIp),
                    current.AutoPcIp,
                    DeviceSerial = MaskSerial(current.DeviceSerial),
                    current.RtpPort,
                    current.UsbPort,
                    current.ObsPort,
                    current.PreviewPort,
                    current.Fps,
                    current.BitrateMbps,
                    current.CameraKey,
                    current.Width,
                    current.Height,
                    current.Preview,
                    current.Obs,
                    current.VirtualCamera,
                    current.LowLatency,
                    current.WifiLimitMbps,
                    current.AdaptiveBitrate,
                    current.MinimumWifiBitrateMbps,
                    current.PowerMode,
                    current.ScreenOff,
                    current.Torch,
                    current.Zoom,
                    current.ForceSamsungLegacy,
                    current.Focus,
                    current.Exposure,
                    current.Wb
                }, new JsonSerializerOptions { WriteIndented = true });
                AddZipText(zip, "settings.json", sanitized);
                AddZipText(zip, "session.log", LogBox.Text);
                var sysInfo = $"OS: {Environment.OSVersion}\n" +
                              $"64-bit OS: {Environment.Is64BitOperatingSystem}\n" +
                              $"App Version: {DiagnosticsReport.AppVersion}\n" +
                              $"ADB: {ToolPaths.Find("adb.exe", settings.AdbPath)}\n" +
                              $"FFmpeg: {ToolPaths.Find("ffmpeg.exe", settings.FfmpegPath)}\n" +
                              $"FFplay: {ToolPaths.Find("ffplay.exe", settings.FfplayPath)}\n" +
                              $"Virtual Camera: {VirtualCameraDriver.GetStatus().Message}\n";
                AddZipText(zip, "system.txt", sysInfo);
                if (capabilities != null) {
                    AddZipText(zip, "capabilities.json", JsonSerializer.Serialize(capabilities, new JsonSerializerOptions { WriteIndented = true }));
                }
            }

            Log(L.Format("s_e494ae446951", zipPath));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true });
        } catch (Exception ex) { Log(L.Get("s_83b25b333786") + ex.Message); }
    }

    public static string MaskIp(string ip) {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        var parts = ip.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "***";
    }

    public static string MaskSerial(string serial) {
        if (string.IsNullOrWhiteSpace(serial)) return "";
        return serial.Length > 4 ? serial[..2] + "***" + serial[^2..] : "***";
    }

    private static void AddZipText(System.IO.Compression.ZipArchive zip, string entryName, string content) {
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private void LatencyTest_Click(object sender, RoutedEventArgs e) {
        try {
            var win = new Window {
                Title = L.Get("s_ad80f438b169"),
                Width = 500,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.Black
            };
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var box = new System.Windows.Shapes.Rectangle { Width = 260, Height = 260, Fill = Brushes.White, Margin = new Thickness(0, 0, 0, 16) };
            var text = new TextBlock { FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            var hint = new TextBlock { Text = L.Get("s_5a920fd3d452"), Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(box);
            panel.Children.Add(text);
            panel.Children.Add(hint);
            win.Content = panel;

            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            var counter = 0;
            timer.Tick += (_, _) => {
                counter++;
                box.Fill = (counter % 60 < 30) ? Brushes.White : Brushes.Red;
                text.Text = DateTime.Now.ToString("HH:mm:ss.fff");
            };
            win.Closed += (_, _) => timer.Stop();
            timer.Start();
            win.Show();
            Log(L.Get("s_cd3e982aa118"));
        } catch (Exception ex) { Log(L.Get("s_4fe9b2abdca2") + ex.Message); }
    }



    private void PrivacyMute_Click(object sender, RoutedEventArgs e) {
        settings.PrivacyMute = !settings.PrivacyMute;
        PrivacyMuteButton.Background = settings.PrivacyMute ? new SolidColorBrush(Color.FromRgb(220, 60, 60)) : null;
        PrivacyMuteButton.Content = settings.PrivacyMute ? "🔒 MUTED" : "🔒 MUTE";
        Log(settings.PrivacyMute ? L.Get("s_d9d09ce574c9") : L.Get("s_ba688cee6c83"));
        QueueSave();
        ApplyControls();
    }

    private string CurrentZoomLabel() => ZoomChoice.SelectedIndex switch {
        1 => "1.2x",
        2 => "1.5x",
        3 => "2.0x",
        4 => "3.0x",
        _ => "1.0x"
    };

    private void AdjustZoom(int delta) {
        var current = ZoomChoice.SelectedIndex;
        if (current < 0) current = 0;
        var next = Math.Clamp(current + delta, 0, ZoomChoice.Items.Count - 1);
        if (next != current) {
            ZoomChoice.SelectedIndex = next;
            var label = CurrentZoomLabel();
            Log(L.Format("s_85502b9c0f15", label));
            QueueSave();
            previewWindow?.UpdateZoom(label);
            UpdatePreviewResolutionBadge();
        }
    }

    private void ResetZoom() {
        if (ZoomChoice.SelectedIndex != 0) {
            ZoomChoice.SelectedIndex = 0;
            Log(L.Get("s_05749525fab4"));
            QueueSave();
            previewWindow?.UpdateZoom("1.0x");
            UpdatePreviewResolutionBadge();
        }
    }

    private void ToggleAutoFraming() {
        AutoFraming.IsChecked = !(AutoFraming.IsChecked == true);
        var enabled = AutoFraming.IsChecked == true;
        UpdateAutoFramingUI(enabled);
        Log(enabled ? L.Get("s_8f93df3d20cd") : L.Get("s_262b38457974"));
        QueueSave();
    }

    private void AutoFraming_Click(object sender, RoutedEventArgs e) => ToggleAutoFraming();

    private void UpdateAutoFramingUI(bool enabled) {
        AutoFramingButton.Content = enabled ? "🤖 CENTER ON" : "🤖 CENTER OFF";
        AutoFramingButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(30, 65, 80)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        AutoFramingButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void AutoFramingSliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (filling || settings == null || AutoFramingZoomSlider == null || AutoFramingSpeedSlider == null || AutoFramingDeadzoneSlider == null) return;
        UpdateAutoFramingSlidersText();
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private void UpdateAutoFramingSlidersText() {
        if (AutoFramingZoomText == null || AutoFramingSpeedText == null || AutoFramingDeadzoneText == null) return;
        var zoom = AutoFramingZoomSlider.Value;
        var speed = AutoFramingSpeedSlider.Value;
        var deadzone = AutoFramingDeadzoneSlider.Value;

        var zoomPlan = zoom switch {
            < 1.15 => L.Get("s_5d198941bf3d"),
            < 1.45 => L.Get("s_28b05fe8f03c"),
            < 1.85 => L.Get("s_c61010b68ba6"),
            _ => L.Get("s_ecaca576a9d2")
        };
        AutoFramingZoomText.Text = $"{zoom:0.00}x · {zoomPlan}";

        var speedDesc = speed switch {
            < 0.6 => L.Get("s_4d149324a48b"),
            < 1.4 => L.Get("s_d39968cd3600"),
            < 2.2 => L.Get("s_05f8c4bd74cf"),
            _ => L.Get("s_239069f41b03")
        };
        AutoFramingSpeedText.Text = $"{speed:0.0}x · {speedDesc}";

        var deadzonePct = (int)Math.Round(deadzone * 100);
        var deadzoneDesc = deadzone switch {
            < 0.03 => L.Get("s_e5d8bb650e0d"),
            < 0.08 => L.Get("s_31f45cf3cd81"),
            _ => L.Get("s_e97ea6fb44e8")
        };
        AutoFramingDeadzoneText.Text = $"{deadzonePct}% · {deadzoneDesc}";
    }

    private void PresetCinema_Click(object sender, RoutedEventArgs e) {
        AutoFramingZoomSlider.Value = 1.25;
        AutoFramingSpeedSlider.Value = 0.5;
        AutoFramingDeadzoneSlider.Value = 0.08;
        AutoFraming.IsChecked = true;
        FaceTracking.IsChecked = true;
        UpdateAutoFramingSlidersText();
        Log(L.Get("s_9035f57f748f"));
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private void PresetBalanced_Click(object sender, RoutedEventArgs e) {
        AutoFramingZoomSlider.Value = 1.35;
        AutoFramingSpeedSlider.Value = 1.0;
        AutoFramingDeadzoneSlider.Value = 0.05;
        AutoFraming.IsChecked = true;
        FaceTracking.IsChecked = true;
        UpdateAutoFramingSlidersText();
        Log(L.Get("s_b881b223a516"));
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private void PresetDynamic_Click(object sender, RoutedEventArgs e) {
        AutoFramingZoomSlider.Value = 1.55;
        AutoFramingSpeedSlider.Value = 1.8;
        AutoFramingDeadzoneSlider.Value = 0.03;
        AutoFraming.IsChecked = true;
        FaceTracking.IsChecked = true;
        UpdateAutoFramingSlidersText();
        Log(L.Get("s_d4fdca0b6aa9"));
        QueueSave();
        controlsDebounceTimer.Stop();
        controlsDebounceTimer.Start();
    }

    private void ColorSliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (filling || settings == null || BrightnessSlider == null || ContrastSlider == null || SaturationSlider == null) return;
        UpdateColorSlidersText();
        QueueSave();
    }

    private void UpdateColorSlidersText() {
        if (BrightnessValueText == null || ContrastValueText == null || SaturationValueText == null) return;
        BrightnessValueText.Text = BrightnessSlider.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        ContrastValueText.Text = ContrastSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        SaturationValueText.Text = SaturationSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e) {
        BrightnessSlider.Value = 0.0;
        ContrastSlider.Value = 1.0;
        SaturationSlider.Value = 1.0;
        UpdateColorSlidersText();
        Log(L.Get("s_785ff47f035c"));
        QueueSave();
    }

    private void BackgroundEffectChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (filling) return;
        if (CustomBgPanel != null) {
            CustomBgPanel.Visibility = (BackgroundEffectChoice.SelectedIndex == 4) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (BackgroundEffectChoice.SelectedIndex == 3) {
            Log(L.Get("s_73dfa86e8a5f"));
        }
        QueueSave();
    }

    private void BrowseCustomBg_Click(object sender, RoutedEventArgs e) {
        var dlg = new Microsoft.Win32.OpenFileDialog {
            Title = L.Get("s_674ba47f3b08"),
            Filter = L.Get("s_19d41186dedb"),
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true) {
            CustomBgPathBox.Text = dlg.FileName;
            Log(L.Format("s_d957251cd4c5", Path.GetFileName(dlg.FileName)));
            QueueSave();
        }
    }

    private void AiFeatherSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (filling) return;
        if (AiFeatherText != null && AiFeatherSlider != null) {
            AiFeatherText.Text = $"{(int)(AiFeatherSlider.Value * 100)}%";
        }
        QueueSave();
    }

    private void SuperResolutionSharpnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (filling) return;
        if (SuperResolutionSharpnessText != null && SuperResolutionSharpnessSlider != null) {
            SuperResolutionSharpnessText.Text = $"{(int)(SuperResolutionSharpnessSlider.Value * 100)}%";
        }
        QueueSave();
    }

    private void ToggleFaceTracking() {
        FaceTracking.IsChecked = !(FaceTracking.IsChecked == true);
        var enabled = FaceTracking.IsChecked == true;
        UpdateFaceTrackingUI(enabled);
        Log(enabled ? L.Get("s_690b5eb67f02") : L.Get("s_57d62c363084"));
        QueueSave();
    }

    private void FaceTracking_Click(object sender, RoutedEventArgs e) => ToggleFaceTracking();

    private void UpdateFaceTrackingUI(bool enabled) {
        FaceTrackingButton.Content = enabled ? "👤 FACE ON" : "👤 FACE OFF";
        FaceTrackingButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(28, 60, 50)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        FaceTrackingButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void FaceOverlay_Click(object sender, RoutedEventArgs e) {
        faceOverlayEnabled = !faceOverlayEnabled;
        settings.ShowFaceOverlay = faceOverlayEnabled;
        UpdateFaceOverlayButtonUI();
        if (!faceOverlayEnabled) {
            FaceCanvas.Children.Clear();
        }
        QueueSave();
    }

    private void UpdateFaceOverlayButtonUI() {
        FaceOverlayButton.Background = faceOverlayEnabled ? new SolidColorBrush(Color.FromRgb(21, 58, 69)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        FaceOverlayButton.Foreground = faceOverlayEnabled ? new SolidColorBrush(Color.FromRgb(0, 229, 255)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void UpdateFaceOverlay(FaceTrackingInfo? face) {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.Invoke(() => UpdateFaceOverlay(face));
            return;
        }

        FaceCanvas.Children.Clear();
        if (face == null || !faceOverlayEnabled) return;

        var canvasW = FaceCanvas.ActualWidth;
        var canvasH = FaceCanvas.ActualHeight;
        if (canvasW < 20 || canvasH < 20) return;

        // Auto-framing crop rectangle guide
        if (face.CropZoom > 1.02f) {
            var scale = 1.0f / Math.Clamp(face.CropZoom, 1.0f, 4.0f);
            var cropW = canvasW * scale;
            var cropH = canvasH * scale;
            var cropLeft = (face.CropCx * canvasW) - (cropW / 2.0);
            var cropTop = (face.CropCy * canvasH) - (cropH / 2.0);

            var cropBorder = new System.Windows.Shapes.Rectangle {
                Width = Math.Max(10, cropW),
                Height = Math.Max(10, cropH),
                Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 4, RadiusY = 4
            };
            Canvas.SetLeft(cropBorder, Math.Clamp(cropLeft, 0, canvasW - cropW));
            Canvas.SetTop(cropBorder, Math.Clamp(cropTop, 0, canvasH - cropH));
            FaceCanvas.Children.Add(cropBorder);
        }

        // Face detection bounding box
        if (face.Width > 0.02f && face.Height > 0.02f) {
            var boxW = face.Width * canvasW;
            var boxH = face.Height * canvasH;
            var left = (face.X * canvasW) - (boxW / 2.0);
            var top = (face.Y * canvasH) - (boxH / 2.0);

            var faceRect = new System.Windows.Shapes.Rectangle {
                Width = Math.Max(16, boxW),
                Height = Math.Max(16, boxH),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                StrokeThickness = 2.0,
                RadiusX = 6, RadiusY = 6
            };
            Canvas.SetLeft(faceRect, Math.Clamp(left, 0, canvasW - boxW));
            Canvas.SetTop(faceRect, Math.Clamp(top, 0, canvasH - boxH));
            FaceCanvas.Children.Add(faceRect);

            string labelText;
            Color badgeColor;
            if (face.FaceCount > 1) {
                labelText = L.Format("s_67392db825a7", face.FaceCount, face.CropZoom);
                badgeColor = Color.FromRgb(255, 179, 0);
            } else if (settings.AutoFraming) {
                labelText = $"🎯 TRACKING {face.CropZoom:F1}x";
                badgeColor = Color.FromRgb(0, 229, 255);
            } else {
                labelText = "👤 FACE AF";
                badgeColor = Color.FromRgb(112, 229, 195);
            }

            var badge = new Border {
                Background = new SolidColorBrush(Color.FromArgb(200, 11, 22, 35)),
                BorderBrush = new SolidColorBrush(badgeColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock {
                    Text = labelText,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(badgeColor)
                }
            };
            Canvas.SetLeft(badge, Math.Clamp(left, 2, Math.Max(2, canvasW - 120)));
            Canvas.SetTop(badge, Math.Max(2, top - 20));
            FaceCanvas.Children.Add(badge);
        }
    }

    private void ToggleHud_Click(object sender, RoutedEventArgs e) {
        if (hudWindow == null) {
            hudWindow = new StreamHudWindow();
            hudWindow.ClosedByUser += () => {
                settings.ShowHud = false;
                QueueSave();
                UpdateHudButtonUI();
            };
            hudWindow.PositionChanged += (l, t) => {
                settings.HudLeft = l;
                settings.HudTop = t;
                QueueSave();
            };
            if (settings.HudLeft > 0 && settings.HudTop > 0) {
                hudWindow.Left = settings.HudLeft;
                hudWindow.Top = settings.HudTop;
            } else {
                hudWindow.Left = Math.Max(40, SystemParameters.WorkArea.Right - 280);
                hudWindow.Top = 40;
            }
        }

        if (hudWindow.IsVisible) {
            hudWindow.Hide();
            settings.ShowHud = false;
        } else {
            hudWindow.Show();
            settings.ShowHud = true;
        }
        QueueSave();
        UpdateHudButtonUI();
    }

    private void UpdateHudButtonUI() {
        var isShown = hudWindow?.IsVisible == true;
        StreamHudButton.Background = isShown ? new SolidColorBrush(Color.FromRgb(28, 64, 82)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        StreamHudButton.Foreground = isShown ? new SolidColorBrush(Color.FromRgb(0, 229, 255)) : new SolidColorBrush(Color.FromRgb(112, 229, 195));
    }

    private void PresetStreaming_Click(object sender, RoutedEventArgs e) => ApplyPreset("streaming");
    private void PresetConference_Click(object sender, RoutedEventArgs e) => ApplyPreset("conference");
    private void PresetPro_Click(object sender, RoutedEventArgs e) => ApplyPreset("pro");
    private void PresetEco_Click(object sender, RoutedEventArgs e) => ApplyPreset("eco");

    public void ApplyPreset(string preset) {
        filling = true;
        try {
            switch (preset.ToLowerInvariant()) {
                case "streaming":
                    Bitrate.Text = "18";
                    CodecChoice.SelectedIndex = 1; // HEVC
                    LowLatency.IsChecked = true;
                    SpoutOutput.IsChecked = true;
                    ObsOutput.IsChecked = false;
                    VirtualCamera.IsChecked = false;
                    AutoFraming.IsChecked = true;
                    AutoFramingZoomSlider.Value = 1.35;
                    AutoFramingSpeedSlider.Value = 1.0;
                    PowerMode.SelectedIndex = 1; // Balanced
                    BackgroundEffectChoice.SelectedIndex = 0; // none
                    SkinSmoothing.IsChecked = false;
                    UpdateAutoFramingUI(true);
                    SelectClosestResolution(1920, 1080);
                    SelectClosestFps(60);
                    Log(L.Get("s_346cf91ccc3f"));
                    break;

                case "conference":
                    Bitrate.Text = "6";
                    CodecChoice.SelectedIndex = 0; // H.264
                    LowLatency.IsChecked = true;
                    SpoutOutput.IsChecked = false;
                    ObsOutput.IsChecked = false;
                    VirtualCamera.IsChecked = true;
                    AutoFraming.IsChecked = false;
                    PowerMode.SelectedIndex = 1; // Balanced
                    BackgroundEffectChoice.SelectedIndex = 1; // Bokeh
                    SkinSmoothing.IsChecked = true;
                    UpdateAutoFramingUI(false);
                    SelectClosestResolution(1280, 720);
                    SelectClosestFps(30);
                    Log(L.Get("s_cbe1833f5c5e"));
                    break;

                case "pro":
                    Bitrate.Text = "25";
                    CodecChoice.SelectedIndex = 1; // HEVC
                    LowLatency.IsChecked = true;
                    SpoutOutput.IsChecked = true;
                    ObsOutput.IsChecked = false;
                    VirtualCamera.IsChecked = false;
                    PowerMode.SelectedIndex = 0; // Maximum
                    BackgroundEffectChoice.SelectedIndex = 0; // none
                    SkinSmoothing.IsChecked = false;
                    SelectClosestResolution(2560, 1440);
                    SelectClosestFps(30);
                    Log(L.Get("s_4c84daabcbd8"));
                    break;

                case "eco":
                    Bitrate.Text = "4";
                    CodecChoice.SelectedIndex = 0; // H.264
                    LowLatency.IsChecked = false;
                    SpoutOutput.IsChecked = false;
                    ObsOutput.IsChecked = false;
                    VirtualCamera.IsChecked = true;
                    AutoFraming.IsChecked = false;
                    PowerMode.SelectedIndex = 2; // Saving
                    ScreenOff.IsChecked = true;
                    BatteryProtect.IsChecked = true;
                    UpdateAutoFramingUI(false);
                    SelectClosestResolution(1280, 720);
                    SelectClosestFps(30);
                    Log(L.Get("s_f84580cc6849"));
                    break;
            }
        } finally {
            filling = false;
        }

        QueueSave();
        ApplyControls();
        PopulateCustomPresets(resetSelection: true);
        UpdateModeUI();
    }

    public void ApplyCustomPreset(string name, Settings preset) {
        filling = true;
        try {
            settings.CopyCapturePropertiesFrom(preset);
            Fill();
            SelectClosestResolution(settings.Width, settings.Height);
            SelectClosestFps(settings.Fps);
        } finally {
            filling = false;
        }

        PopulateCustomPresets(name);
        QueueSave();
        ApplyControls();
        Log(L.Format("s_2f11f0889a15", name));
    }

    private void PopulateCustomPresets(string? selectedName = null, bool resetSelection = false) {
        if (CustomPresetsCombo == null) return;
        customPresetsFilling = true;
        try {
            var toSelect = resetSelection ? null : (selectedName ?? (CustomPresetsCombo.SelectedItem as string));
            CustomPresetsCombo.Items.Clear();
            if (settings.CustomPresets == null || settings.CustomPresets.Count == 0) {
                CustomPresetsCombo.Items.Add(new ComboBoxItem { Content = L.Get("s_ca7f009d9302"), IsEnabled = false });
                CustomPresetsCombo.SelectedIndex = 0;
                if (DeletePresetButton != null) DeletePresetButton.IsEnabled = false;
                return;
            }

            CustomPresetsCombo.Items.Add(new ComboBoxItem { Content = L.Format("s_6a51703b156f", settings.CustomPresets.Count), IsEnabled = false });

            var targetIndex = -1;
            var index = 1;
            foreach (var key in settings.CustomPresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
                CustomPresetsCombo.Items.Add(key);
                if (toSelect != null && string.Equals(key, toSelect, StringComparison.OrdinalIgnoreCase)) {
                    targetIndex = index;
                }
                index++;
            }

            if (targetIndex >= 0) {
                CustomPresetsCombo.SelectedIndex = targetIndex;
                if (DeletePresetButton != null) DeletePresetButton.IsEnabled = true;
            } else {
                CustomPresetsCombo.SelectedIndex = 0;
                if (DeletePresetButton != null) DeletePresetButton.IsEnabled = false;
            }
        } finally {
            customPresetsFilling = false;
        }
    }

    private void CustomPresetsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (customPresetsFilling || settings == null) return;
        if (CustomPresetsCombo.SelectedItem is string presetName && settings.CustomPresets.TryGetValue(presetName, out var preset)) {
            if (DeletePresetButton != null) DeletePresetButton.IsEnabled = true;
            ApplyCustomPreset(presetName, preset);
        } else {
            if (DeletePresetButton != null) DeletePresetButton.IsEnabled = false;
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e) {
        if (SavePresetModal == null || PresetNameInput == null) return;
        var defaultName = CustomPresetsCombo.SelectedItem is string curName && !string.IsNullOrWhiteSpace(curName)
            ? curName
            : L.Format("s_c45a9c6396b6", settings.CustomPresets.Count + 1);
        PresetNameInput.Text = defaultName;
        SavePresetModal.Visibility = Visibility.Visible;
        PresetNameInput.SelectAll();
        PresetNameInput.Focus();
    }

    private void CancelSavePreset_Click(object sender, RoutedEventArgs e) {
        if (SavePresetModal != null) SavePresetModal.Visibility = Visibility.Collapsed;
    }

    private void PresetNameInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == System.Windows.Input.Key.Enter) {
            ConfirmSavePreset_Click(sender, e);
            e.Handled = true;
        } else if (e.Key == System.Windows.Input.Key.Escape) {
            CancelSavePreset_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ConfirmSavePreset_Click(object sender, RoutedEventArgs e) {
        var name = PresetNameInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) {
            System.Windows.MessageBox.Show(L.Get("s_635bd5f4b365"), L.Get("s_3eb16b52e926"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SavePresetModal != null) SavePresetModal.Visibility = Visibility.Collapsed;

        var current = Read();
        var clone = current.Clone();
        clone.CustomPresets.Clear();
        settings.CustomPresets[name] = clone;
        settings.Save();

        PopulateCustomPresets(name);
        Log(L.Format("s_7fcd24ec4223", name));
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e) {
        if (CustomPresetsCombo?.SelectedItem is not string presetName || string.IsNullOrWhiteSpace(presetName)) return;
        var confirm = System.Windows.MessageBox.Show(
            L.Format("s_4e31096897cd", presetName),
            L.Get("s_6076fb477cd6"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (settings.CustomPresets.Remove(presetName)) {
            settings.Save();
            PopulateCustomPresets(resetSelection: true);
            Log(L.Format("s_2b7bf7166ebc", presetName));
        }
    }

    private void SelectClosestResolution(int w, int h) {
        if (ResolutionChoice.ItemsSource is IEnumerable<VideoCapability> caps) {
            var match = caps.FirstOrDefault(c => c.Width == w && c.Height == h)
                     ?? caps.OrderBy(c => Math.Abs(c.Width * c.Height - w * h)).FirstOrDefault();
            if (match != null) ResolutionChoice.SelectedItem = match;
        }
    }

    private void SelectClosestFps(int targetFps) {
        if (Fps.ItemsSource is IEnumerable<FpsChoice> list) {
            var match = list.FirstOrDefault(f => f.Value == targetFps)
                     ?? list.OrderBy(f => Math.Abs(f.Value - targetFps)).FirstOrDefault();
            if (match != null) Fps.SelectedItem = match;
        }
    }

    private void OptimizeObs_Click(object sender, RoutedEventArgs e) => OptimizeObs();

    private void OptimizeObs() {
        MessageBox.Show(
            L.Get("s_86c549c04690") +
            L.Get("s_e69c60c61ad5") +
            L.Get("s_7958044bfbab") +
            L.Get("s_25cc81e02e48") +
            $"udp://127.0.0.1:{settings.ObsPort}?fifo_size=4096&overrun_nonfatal=1\n\n" +
            L.Get("s_a6a4bb24151e"),
            L.Get("s_3c4aa27da29e"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyControls() {
        if (engine?.Running == true) {
            controlsDebounceTimer.Stop();
            var current = Read();
            thermalGuard?.UpdateSettings(current);
            _ = engine.UpdateControls(current, CancellationToken.None);
        }
    }

    private void ToggleLockAeAwb() {
        LockAeAwb.IsChecked = !(LockAeAwb.IsChecked == true);
        var locked = LockAeAwb.IsChecked == true;
        UpdateLockAeAwbUI(locked);
        Log(locked ? L.Get("s_d15097c0dc88") : L.Get("s_98504c092c56"));
        QueueSave();
    }

    private void LockAeAwb_Click(object sender, RoutedEventArgs e) => ToggleLockAeAwb();

    private void UpdateLockAeAwbUI(bool locked) {
        LockAeAwbButton.Content = locked ? "🔒 AE/AWB LOCKED" : "🔓 AE/AWB";
        LockAeAwbButton.Background = locked ? new SolidColorBrush(Color.FromRgb(40, 75, 110)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        LockAeAwbButton.Foreground = locked ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
        previewWindow?.UpdateLockState(locked);
    }

    private void UpdatePreviewResolutionBadge() {
        if (previewBitmap != null) {
            var zoomStr = ZoomChoice.SelectedIndex > 0 ? $" · {CurrentZoomLabel()}" : "";
            PreviewResolutionBadge.Text = $"{previewBitmap.PixelWidth}×{previewBitmap.PixelHeight}{zoomStr}";
        }
    }

    private void VideoPreview_MouseWheel(object sender, MouseWheelEventArgs e) {
        AdjustZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void RenderPreview(byte[] buffer, int width, int height) {
        if (closing || closed) return;
        try {
            if (previewBitmap == null || previewBitmap.PixelWidth != width || previewBitmap.PixelHeight != height) {
                previewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                LivePreviewImage.Source = previewBitmap;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                ResolutionBadgeBorder.Visibility = Visibility.Visible;
                UpdatePreviewResolutionBadge();
                if (gridEnabled) RedrawGrid();
            }
            previewBitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);
            previewWindow?.RenderFrame(buffer, width, height);
        } catch { }
    }

    private void VideoPreview_MouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Middle || e.ClickCount >= 2) {
            ResetZoom();
            e.Handled = true;
            return;
        }
        if (engine?.Running != true || previewBitmap == null) return;
        var pos = e.GetPosition(LivePreviewImage);
        var imgW = LivePreviewImage.ActualWidth;
        var imgH = LivePreviewImage.ActualHeight;
        var vidW = (double)previewBitmap.PixelWidth;
        var vidH = (double)previewBitmap.PixelHeight;

        if (imgW <= 0 || imgH <= 0 || vidW <= 0 || vidH <= 0) return;

        var scale = Math.Min(imgW / vidW, imgH / vidH);
        var renderedW = vidW * scale;
        var renderedH = vidH * scale;
        var offsetX = (imgW - renderedW) / 2.0;
        var offsetY = (imgH - renderedH) / 2.0;

        var videoX = pos.X - offsetX;
        var videoY = pos.Y - offsetY;

        if (videoX < 0 || videoX > renderedW || videoY < 0 || videoY > renderedH) return;

        var dispX = (float)(videoX / renderedW);
        var dispY = (float)(videoY / renderedH);

        var s = Read();
        float tx = dispX, ty = dispY;
        switch (s.Rotation) {
            case 90: tx = dispY; ty = 1.0f - dispX; break;
            case 180: tx = 1.0f - dispX; ty = 1.0f - dispY; break;
            case 270: tx = 1.0f - dispY; ty = dispX; break;
        }
        if (s.FlipHorizontal) tx = 1.0f - tx;

        var sensorX = Math.Clamp(tx, 0.05f, 0.95f);
        var sensorY = Math.Clamp(ty, 0.05f, 0.95f);

        _ = engine.TapFocus(sensorX, sensorY, CancellationToken.None);
        DrawFocusReticle(e.GetPosition(FocusCanvas));
        Log(L.Format("s_6546db1c5d9e", dispX, dispY, sensorX, sensorY));
    }

    private void DrawFocusReticle(Point pt) {
        FocusCanvas.Children.Clear();
        var brush = new SolidColorBrush(Color.FromRgb(66, 216, 178));

        var ring = new System.Windows.Shapes.Ellipse {
            Width = 44, Height = 44,
            Stroke = brush, StrokeThickness = 2
        };
        Canvas.SetLeft(ring, pt.X - 22);
        Canvas.SetTop(ring, pt.Y - 22);
        FocusCanvas.Children.Add(ring);

        var dot = new System.Windows.Shapes.Ellipse {
            Width = 6, Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(112, 229, 195))
        };
        Canvas.SetLeft(dot, pt.X - 3);
        Canvas.SetTop(dot, pt.Y - 3);
        FocusCanvas.Children.Add(dot);

        void AddTick(double x1, double y1, double x2, double y2) {
            FocusCanvas.Children.Add(new System.Windows.Shapes.Line {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush, StrokeThickness = 2
            });
        }
        AddTick(pt.X, pt.Y - 24, pt.X, pt.Y - 14);
        AddTick(pt.X, pt.Y + 14, pt.X, pt.Y + 24);
        AddTick(pt.X - 24, pt.Y, pt.X - 14, pt.Y);
        AddTick(pt.X + 14, pt.Y, pt.X + 24, pt.Y);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) => {
            timer.Stop();
            FocusCanvas.Children.Clear();
        };
        timer.Start();
    }

    private void ToggleGrid() {
        UpdateGridUI(!gridEnabled);
        QueueSave();
    }

    private void Grid_Click(object sender, RoutedEventArgs e) => ToggleGrid();

    private void UpdateGridUI(bool enabled) {
        gridEnabled = enabled;
        GridButton.Content = enabled ? "📐 GRID ON" : "📐 GRID";
        GridButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(28, 58, 50)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        GridButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
        GridCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) RedrawGrid();
        else GridCanvas.Children.Clear();
        previewWindow?.UpdateGridState(enabled);
    }

    private void TogglePeaking() {
        UpdatePeakingUI(!focusPeakingEnabled);
        QueueSave();
    }

    private void Peak_Click(object sender, RoutedEventArgs e) => TogglePeaking();

    private void UpdatePeakingUI(bool enabled) {
        focusPeakingEnabled = enabled;
        PeakButton.Content = enabled ? "🔍 PEAK ON" : "🔍 PEAK";
        PeakButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(28, 58, 50)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        PeakButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
        previewWindow?.UpdatePeakingState(enabled);
    }

    private void ToggleZebra() {
        UpdateZebraUI(!zebraEnabled);
        QueueSave();
    }

    private void Zebra_Click(object sender, RoutedEventArgs e) => ToggleZebra();

    private void UpdateZebraUI(bool enabled) {
        zebraEnabled = enabled;
        ZebraButton.Content = enabled ? "🦓 ZEBRA ON" : "🦓 ZEBRA";
        ZebraButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(58, 48, 28)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        ZebraButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(255, 204, 102)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
        previewWindow?.UpdateZebraState(enabled);
    }

    private void GridCanvas_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (gridEnabled) RedrawGrid();
    }

    private void RedrawGrid() {
        if (!gridEnabled) return;
        var vidW = previewBitmap != null ? previewBitmap.PixelWidth : (settings?.Width > 0 ? settings.Width : 1920);
        var vidH = previewBitmap != null ? previewBitmap.PixelHeight : (settings?.Height > 0 ? settings.Height : 1080);
        DrawCompositionGrid(GridCanvas, LivePreviewImage, vidW, vidH);
    }

    public static void DrawCompositionGrid(Canvas canvas, Image image, int vidWidth, int vidHeight) {
        canvas.Children.Clear();
        var imgW = image.ActualWidth;
        var imgH = image.ActualHeight;
        if (imgW <= 10 || imgH <= 10 || vidWidth <= 0 || vidHeight <= 0) return;

        var scale = Math.Min(imgW / vidWidth, imgH / vidHeight);
        var renderedW = vidWidth * scale;
        var renderedH = vidHeight * scale;
        var offsetX = (imgW - renderedW) / 2.0;
        var offsetY = (imgH - renderedH) / 2.0;

        var lineBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        var pointBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
        var accentBrush = new SolidColorBrush(Color.FromArgb(170, 112, 229, 195));

        // Rule of thirds lines
        var x1 = offsetX + renderedW / 3.0;
        var x2 = offsetX + renderedW * 2.0 / 3.0;
        var y1 = offsetY + renderedH / 3.0;
        var y2 = offsetY + renderedH * 2.0 / 3.0;

        void AddLine(double lx1, double ly1, double lx2, double ly2, Brush brush, double thickness = 1.0, DoubleCollection? dashes = null) {
            var line = new System.Windows.Shapes.Line {
                X1 = lx1, Y1 = ly1, X2 = lx2, Y2 = ly2,
                Stroke = brush, StrokeThickness = thickness,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            if (dashes != null) line.StrokeDashArray = dashes;
            canvas.Children.Add(line);
        }

        // Horizontal thirds
        AddLine(offsetX, y1, offsetX + renderedW, y1, lineBrush);
        AddLine(offsetX, y2, offsetX + renderedW, y2, lineBrush);

        // Vertical thirds
        AddLine(x1, offsetY, x1, offsetY + renderedH, lineBrush);
        AddLine(x2, offsetY, x2, offsetY + renderedH, lineBrush);

        // Eye Level label on top horizontal line
        var eyeLabel = new TextBlock {
            Text = "EYE LEVEL",
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(eyeLabel, offsetX + 8);
        Canvas.SetTop(eyeLabel, y1 - 13);
        canvas.Children.Add(eyeLabel);

        // Center crosshair (16px)
        var cx = offsetX + renderedW / 2.0;
        var cy = offsetY + renderedH / 2.0;
        AddLine(cx - 8, cy, cx + 8, cy, pointBrush, 1.5);
        AddLine(cx, cy - 8, cx, cy + 8, pointBrush, 1.5);

        // Thirds power intersection dots
        void AddDot(double px, double py) {
            var dot = new System.Windows.Shapes.Ellipse {
                Width = 4, Height = 4,
                Fill = pointBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(dot, px - 2);
            Canvas.SetTop(dot, py - 2);
            canvas.Children.Add(dot);
        }
        AddDot(x1, y1);
        AddDot(x2, y1);
        AddDot(x1, y2);
        AddDot(x2, y2);

        // 9:16 Safe Area (Shorts / Reels / TikTok)
        var w916 = renderedH * (9.0 / 16.0);
        if (w916 < renderedW * 0.92) {
            var left916 = offsetX + (renderedW - w916) / 2.0;
            var right916 = left916 + w916;
            var dashes = new DoubleCollection { 4, 4 };
            AddLine(left916, offsetY, left916, offsetY + renderedH, accentBrush, 1.2, dashes);
            AddLine(right916, offsetY, right916, offsetY + renderedH, accentBrush, 1.2, dashes);

            var shortsLabel = new TextBlock {
                Text = "9:16 SHORTS",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = accentBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(shortsLabel, left916 + 4);
            Canvas.SetTop(shortsLabel, offsetY + 6);
            canvas.Children.Add(shortsLabel);
        }
    }

    private void PopoutPreview_Click(object sender, RoutedEventArgs e) {
        if (previewWindow != null) {
            previewWindow.Activate();
            return;
        }
        // Ensure preview pipeline is active so frames are forwarded
        if (engine != null && Preview.IsChecked != true) {
            filling = true;
            try { Preview.IsChecked = true; } finally { filling = false; }
            engine.SetPreviewEnabled(true);
        }
        previewWindow = new PreviewWindow(Read, (x, y) => {
            if (engine?.Running == true) return engine.TapFocus(x, y, CancellationToken.None);
            return Task.CompletedTask;
        }, delta => Dispatcher.Invoke(() => AdjustZoom(delta)),
           () => Dispatcher.Invoke(ResetZoom),
           () => Dispatcher.Invoke(ToggleLockAeAwb),
           () => Dispatcher.Invoke(ToggleGrid),
           () => Dispatcher.Invoke(TogglePeaking),
           () => Dispatcher.Invoke(ToggleZebra),
           () => Dispatcher.Invoke(TriggerSnapshot),
           () => Dispatcher.Invoke(ToggleRecording));
        previewWindow.Closed += (_, _) => { previewWindow = null; UpdatePreviewActivity(); };
        previewWindow.IsVisibleChanged += (_, _) => UpdatePreviewActivity();
        previewWindow.StateChanged += (_, _) => UpdatePreviewActivity();
        previewWindow.UpdateZoom(CurrentZoomLabel());
        previewWindow.UpdateLockState(LockAeAwb.IsChecked == true);
        previewWindow.UpdateGridState(gridEnabled);
        previewWindow.UpdatePeakingState(focusPeakingEnabled);
        previewWindow.UpdateZebraState(zebraEnabled);
        previewWindow.UpdateRecordState(engine?.IsRecording == true, engine?.RecordElapsed ?? TimeSpan.Zero);
        previewWindow.Show();
        // Pass the current frame immediately so window doesn't start black
        if (previewBitmap != null) {
            var w = previewBitmap.PixelWidth;
            var h = previewBitmap.PixelHeight;
            var stride = w * 4;
            var buf = new byte[stride * h];
            previewBitmap.CopyPixels(buf, stride, 0);
            previewWindow.RenderFrame(buf, w, h);
        }
    }

    private void Ffplay_Click(object sender, RoutedEventArgs e) {
        if (engine?.Running == true) engine.LaunchExternalFfplay();
        else Log(L.Get("s_fa44c07c53bb"));
    }

    private static void UpdateRunOnStartup(bool enabled) {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;
            if (enabled) {
                key.SetValue("H3HCam Receiver", $"\"{exePath}\" --tray");
            } else {
                key.DeleteValue("H3HCam Receiver", false);
                key.DeleteValue("S8Cam Receiver", false);
            }
        } catch { }
    }

    private async void OnClosing(object? sender, CancelEventArgs e) {
        if (closed) return;
        if (!forceExit && settings.MinimizeToTray) {
            e.Cancel = true;
            Hide();
            trayIcon?.ShowBalloon("H3H Cam", L.Get("s_3996f3f5d141"));
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        trayIcon?.Dispose();
        trayIcon = null;
        controlsDebounceTimer.Stop();
        try { previewWindow?.Close(); } catch { }
        previewWindow = null;
        try { hudWindow?.Close(); } catch { }
        hudWindow = null;
        usbWatcherTimer.Stop();
        IsEnabled = false;
        saveTimer.Stop();
        wifiDiscovery?.Dispose();
        wifiDiscovery = null;
        operation?.Cancel();
        try {
            if (!busy) {
                var s = Read();
                if (WindowState == WindowState.Normal) {
                    s.WindowLeft = Left;
                    s.WindowTop = Top;
                    s.WindowWidth = Width;
                    s.WindowHeight = Height;
                }
                s.Save();
            }
            if (engine != null) await engine.DisposeAsync();
        } catch (Exception ex) { Log(L.Get("s_eafe0608cdef") + ex.Message); }
        finally {
            logTimer.Stop();
            closed = true;
            Closing -= OnClosing;
            try {
                System.Windows.Application.Current?.Shutdown();
            } catch { }
        }
    }
}

