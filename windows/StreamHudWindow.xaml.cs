using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace S8Cam;

public partial class StreamHudWindow : Window {
    private double currentOpacity = 0.92;
    public event Action? ClosedByUser;
    public event Action<double, double>? PositionChanged;

    public StreamHudWindow() {
        InitializeComponent();
        Opacity = currentOpacity;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ButtonState == MouseButtonState.Pressed) {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleOpacity_Click(object sender, RoutedEventArgs e) {
        currentOpacity = currentOpacity > 0.85 ? 0.60 : (currentOpacity > 0.5 ? 0.35 : 0.92);
        Opacity = currentOpacity;
    }

    private void CloseHud_Click(object sender, RoutedEventArgs e) {
        ClosedByUser?.Invoke();
        Hide();
    }

    protected override void OnLocationChanged(EventArgs e) {
        base.OnLocationChanged(e);
        if (Left >= 0 && Top >= 0) {
            PositionChanged?.Invoke(Left, Top);
        }
    }

    public void UpdateMetrics(LiveStatus s, Settings settings) {
        HudFps.Text = $"{s.DetectedFps:F1}";
        HudBitrate.Text = $"{s.ReceivedMbps:F1} Mbps";
        HudLatency.Text = $"{s.LatencyMs:F1} ms";

        var phoneStr = s.Thermal;
        if (s.Power != null && s.Power.Percent.HasValue)
            phoneStr = $"{s.Power.Percent}% · {s.Power.TemperatureC:F1}°C";
        HudPhone.Text = string.IsNullOrEmpty(phoneStr) ? "—" : phoneStr;

        HudDrops.Text = s.DroppedFrames > 0 ? $"Drop: {s.DroppedFrames}" : "Drop: 0";
        HudDrops.Foreground = s.DroppedFrames > 0
            ? new SolidColorBrush(Color.FromRgb(255, 83, 112))
            : new SolidColorBrush(Color.FromRgb(136, 146, 176));

        if (settings.SpoutOutput) {
            HudOutput.Text = "SPOUT2 DIRECT";
            HudOutput.Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255));
        } else if (settings.VirtualCamera) {
            HudOutput.Text = "VIRTUAL CAM";
            HudOutput.Foreground = new SolidColorBrush(Color.FromRgb(57, 255, 20));
        } else if (settings.Obs) {
            HudOutput.Text = "OBS MPEG-TS";
            HudOutput.Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 0));
        } else {
            HudOutput.Text = "PREVIEW ONLY";
            HudOutput.Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 176));
        }

        var isLive = s.DetectedFps > 1.0;
        LiveDot.Fill = isLive
            ? new SolidColorBrush(Color.FromRgb(0, 255, 102))
            : new SolidColorBrush(Color.FromRgb(255, 83, 112));
    }
}
