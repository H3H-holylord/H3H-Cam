using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace S8Cam;

public partial class PreviewWindow : Window {
    private readonly Func<Settings> getSettings;
    private readonly Func<float, float, Task> onFocus;
    private readonly Action<int>? onZoomDelta;
    private readonly Action? onZoomReset;
    private readonly Action? onToggleLock;
    private readonly Action? onToggleGrid;
    private readonly Action? onTogglePeak;
    private readonly Action? onToggleZebra;
    private readonly Action? onSnapshot;
    private readonly Action? onToggleRecord;
    private WriteableBitmap? bitmap;
    private string currentZoomLabel = "1.0x";
    private bool isFullscreen;
    private bool gridEnabled;
    private bool peakingEnabled;
    private bool zebraEnabled;
    private byte[]? processedBuffer;
    private WindowState previousState = WindowState.Normal;
    private WindowStyle previousStyle = WindowStyle.SingleBorderWindow;

    public PreviewWindow(Func<Settings> getSettings, Func<float, float, Task> onFocus,
        Action<int>? onZoomDelta = null, Action? onZoomReset = null, Action? onToggleLock = null, Action? onToggleGrid = null,
        Action? onTogglePeak = null, Action? onToggleZebra = null,
        Action? onSnapshot = null, Action? onToggleRecord = null) {
        InitializeComponent();
        this.getSettings = getSettings;
        this.onFocus = onFocus;
        this.onZoomDelta = onZoomDelta;
        this.onZoomReset = onZoomReset;
        this.onToggleLock = onToggleLock;
        this.onToggleGrid = onToggleGrid;
        this.onTogglePeak = onTogglePeak;
        this.onToggleZebra = onToggleZebra;
        this.onSnapshot = onSnapshot;
        this.onToggleRecord = onToggleRecord;
        KeyDown += PreviewWindow_KeyDown;
        LivePreviewImage.SizeChanged += (_, _) => { if (gridEnabled) RedrawGrid(); };
    }

    public void UpdateGridState(bool enabled) {
        gridEnabled = enabled;
        GridButton.Content = enabled ? "📐 GRID ON" : "📐 GRID";
        GridButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(28, 58, 50)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        GridButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
        GridCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) RedrawGrid();
    }

    private void Grid_Click(object sender, RoutedEventArgs e) => onToggleGrid?.Invoke();

    private void RedrawGrid() {
        if (!gridEnabled) return;
        var vidW = bitmap != null ? bitmap.PixelWidth : 1920;
        var vidH = bitmap != null ? bitmap.PixelHeight : 1080;
        MainWindow.DrawCompositionGrid(GridCanvas, LivePreviewImage, vidW, vidH);
    }

    private void GridCanvas_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (gridEnabled) RedrawGrid();
    }

    public void UpdatePeakingState(bool enabled) {
        peakingEnabled = enabled;
        PeakButton.Content = enabled ? "🔍 PEAK ON" : "🔍 PEAK";
        PeakButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(28, 58, 50)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        PeakButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void Peak_Click(object sender, RoutedEventArgs e) => onTogglePeak?.Invoke();

    public void UpdateZebraState(bool enabled) {
        zebraEnabled = enabled;
        ZebraButton.Content = enabled ? "🦓 ZEBRA ON" : "🦓 ZEBRA";
        ZebraButton.Background = enabled ? new SolidColorBrush(Color.FromRgb(58, 48, 28)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        ZebraButton.Foreground = enabled ? new SolidColorBrush(Color.FromRgb(255, 204, 102)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void Zebra_Click(object sender, RoutedEventArgs e) => onToggleZebra?.Invoke();

    public void UpdateLockState(bool locked) {
        LockAeAwbButton.Content = locked ? "🔒 AE/AWB LOCKED" : "🔓 AE/AWB";
        LockAeAwbButton.Background = locked ? new SolidColorBrush(Color.FromRgb(40, 75, 110)) : new SolidColorBrush(Color.FromRgb(21, 36, 53));
        LockAeAwbButton.Foreground = locked ? new SolidColorBrush(Color.FromRgb(112, 229, 195)) : new SolidColorBrush(Color.FromRgb(132, 152, 180));
    }

    private void LockAeAwb_Click(object sender, RoutedEventArgs e) => onToggleLock?.Invoke();

    private void Snapshot_Click(object sender, RoutedEventArgs e) => onSnapshot?.Invoke();

    private void Record_Click(object sender, RoutedEventArgs e) => onToggleRecord?.Invoke();

    public void TriggerFlash() {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(250));
        CameraFlashOverlay?.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    public void UpdateRecordState(bool isRecording, TimeSpan elapsed) {
        if (RecBadgeBorder != null)
            RecBadgeBorder.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        if (RecBadgeText != null && isRecording)
            RecBadgeText.Text = $"🔴 REC {elapsed:mm\\:ss}";
        if (RecordButton != null) {
            RecordButton.Content = isRecording ? $"⏹️ СТОП ({elapsed:mm\\:ss})" : "🔴 ЗАПИСЬ";
            RecordButton.Background = isRecording
                ? new SolidColorBrush(Color.FromRgb(90, 18, 26))
                : new SolidColorBrush(Color.FromRgb(42, 20, 26));
            RecordButton.Foreground = isRecording
                ? new SolidColorBrush(Color.FromRgb(255, 77, 109))
                : new SolidColorBrush(Color.FromRgb(255, 107, 139));
        }
    }

    public void UpdateZoom(string label) {
        currentZoomLabel = label;
        if (bitmap != null) {
            var zoomStr = currentZoomLabel != "1.0x" ? $" · {currentZoomLabel}" : "";
            ResolutionBadge.Text = $"{bitmap.PixelWidth}×{bitmap.PixelHeight}{zoomStr}";
        }
    }

    public void RenderFrame(byte[] buffer, int width, int height) {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => {
            if (bitmap == null || bitmap.PixelWidth != width || bitmap.PixelHeight != height) {
                bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                LivePreviewImage.Source = bitmap;
                var zoomStr = currentZoomLabel != "1.0x" ? $" · {currentZoomLabel}" : "";
                ResolutionBadge.Text = $"{width}×{height}{zoomStr}";
                if (gridEnabled) RedrawGrid();
            }

            var toDraw = buffer;
            if (peakingEnabled || zebraEnabled) {
                var needed = width * height * 4;
                if (processedBuffer == null || processedBuffer.Length != needed) {
                    processedBuffer = new byte[needed];
                }
                VideoOverlayProcessor.ProcessFrame(buffer, processedBuffer, width, height, peakingEnabled, zebraEnabled);
                toDraw = processedBuffer;
            }

            bitmap.WritePixels(new Int32Rect(0, 0, width, height), toDraw, width * 4, 0);
        });
    }

    private void VideoPreview_MouseWheel(object sender, MouseWheelEventArgs e) {
        if (e.Delta > 0) onZoomDelta?.Invoke(1);
        else if (e.Delta < 0) onZoomDelta?.Invoke(-1);
        e.Handled = true;
    }

    private void PreviewWindow_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.Escape && isFullscreen) ToggleFullscreen();
        else if (e.Key == Key.F9 || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)) onSnapshot?.Invoke();
        else if (e.Key == Key.F10 || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.R)) onToggleRecord?.Invoke();
        else if (e.Key is Key.Space or Key.Enter or Key.F) TriggerCenterFocus();
    }

    private void TriggerCenterFocus() {
        if (!IsVisible) return;
        _ = onFocus(0.5f, 0.5f);
        var cx = FocusCanvas.ActualWidth / 2.0;
        var cy = FocusCanvas.ActualHeight / 2.0;
        if (cx > 0 && cy > 0) {
            DrawFocusReticle(new Point(cx, cy));
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen() {
        isFullscreen = !isFullscreen;
        if (isFullscreen) {
            previousState = WindowState;
            previousStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        } else {
            WindowStyle = previousStyle;
            WindowState = previousState;
        }
    }

    private void VideoPreview_MouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Middle || e.ClickCount >= 2) {
            onZoomReset?.Invoke();
            e.Handled = true;
            return;
        }
        if (bitmap == null) return;
        var pos = e.GetPosition(LivePreviewImage);
        var imgW = LivePreviewImage.ActualWidth;
        var imgH = LivePreviewImage.ActualHeight;
        var vidW = (double)bitmap.PixelWidth;
        var vidH = (double)bitmap.PixelHeight;

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

        var s = getSettings();
        float tx = dispX, ty = dispY;
        switch (s.Rotation) {
            case 90: tx = dispY; ty = 1.0f - dispX; break;
            case 180: tx = 1.0f - dispX; ty = 1.0f - dispY; break;
            case 270: tx = 1.0f - dispY; ty = dispX; break;
        }
        if (s.FlipHorizontal) tx = 1.0f - tx;

        var sensorX = Math.Clamp(tx, 0.05f, 0.95f);
        var sensorY = Math.Clamp(ty, 0.05f, 0.95f);

        _ = onFocus(sensorX, sensorY);
        DrawFocusReticle(e.GetPosition(FocusCanvas));
    }

    private void DrawFocusReticle(Point pt) {
        FocusCanvas.Children.Clear();
        var brush = new SolidColorBrush(Color.FromRgb(66, 216, 178));

        var ring = new Ellipse {
            Width = 44, Height = 44,
            Stroke = brush, StrokeThickness = 2,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        Canvas.SetLeft(ring, pt.X - 22);
        Canvas.SetTop(ring, pt.Y - 22);
        FocusCanvas.Children.Add(ring);

        var dot = new Ellipse {
            Width = 6, Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(112, 229, 195))
        };
        Canvas.SetLeft(dot, pt.X - 3);
        Canvas.SetTop(dot, pt.Y - 3);
        FocusCanvas.Children.Add(dot);

        void AddTick(double x1, double y1, double x2, double y2) {
            FocusCanvas.Children.Add(new Line {
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
}
