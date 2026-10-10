using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using S8Cam;

internal static class LocalizationTests {
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Unit() {
        Check(L.Resolve("auto", CultureInfo.GetCultureInfo("ru-RU")) == "ru", "Russian automatic locale");
        Check(L.Resolve("auto", CultureInfo.GetCultureInfo("de-DE")) == "en", "English fallback locale");
        Check(L.Resolve("en", CultureInfo.GetCultureInfo("ru-RU")) == "en", "Explicit English overrides system");
        var ru = L.Catalog("ru"); var en = L.Catalog("en");
        Check(ru.Keys.Order().SequenceEqual(en.Keys.Order()), "Translation resource keys match");
        foreach (var key in ru.Keys) {
            var a = CompositeFormat.Parse(ru[key]); var b = CompositeFormat.Parse(en[key]);
            Check(a.MinimumArgumentCount == b.MinimumArgumentCount, "Format arguments match: " + key);
            Check(!string.IsNullOrWhiteSpace(en[key]), "English translation exists: " + key);
            Check(!Regex.IsMatch(en[key], "[А-Яа-яЁё]"), "English resource contains Russian: " + key);
        }
        var before = new Settings { Language = "ru", Width = 2560, Height = 1440, Fps = 30, CameraKey = "0/2" };
        var after = before.Clone(); after.Language = "en";
        Check(!before.RequiresStreamRestart(after), "Language change does not restart transport/camera");
        L.Configure("en"); // Exercise the remaining tests with English diagnostics as on international PCs.
        Check(L.Get("s_602ade2abfab") == "Auto · main rear camera", "English resource lookup");
        Console.WriteLine("PASS localization: " + en.Count + " matching resources, formats, locale fallback and capture independence");
    }
    public static void Ui(string root) {
        Unit(); Directory.CreateDirectory(root);
        var original = new Settings { Language = "ru", MinimizeToTray = false, Width = 2560, Height = 1440, Fps = 30, BitrateMbps = 32, AutoStart = false };
        original.Save();
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        try {
            var choice = (ComboBox)window.FindName("LanguageChoice");
            var bitrate = (TextBox)window.FindName("Bitrate");
            var resolution = (ComboBox)window.FindName("ResolutionChoice");
            var fps = (ComboBox)window.FindName("Fps");
            var presetName = (TextBox)window.FindName("PresetNameInput");
            presetName.Text = "My custom preset";
            var resolutionItem = resolution.SelectedItem; var fpsItem = fps.SelectedItem;
            foreach (var (index, language) in new[] { (2,"en"), (1,"ru"), (0,"auto"), (2,"en") }) {
                choice.SelectedIndex = index;
                Check(Settings.Load().Language == language, "UI selection persists");
                Check(bitrate.Text == "32" && resolution.SelectedItem == resolutionItem && fps.SelectedItem == fpsItem, "Capture selection survives UI language switch");
                Check(Settings.Load().Width == 2560 && Settings.Load().Height == 1440, "Saved resolution survives language switch");
                Check(presetName.Text == "My custom preset", "Language change preserves user text");
            }
            Check((string)((Button)window.FindName("ScanButton")).Content == "FIND", "English main action");
            Check(((CameraCapability)((ComboBox)window.FindName("CameraChoice")).SelectedItem).DisplayLabel == "Auto · main rear camera", "Camera label changes language without changing its protocol key");
            var panel = (FrameworkElement)window.FindName("BasicPanel");
            panel.Visibility = Visibility.Visible;
            ((FrameworkElement)window.FindName("SettingsPanel")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("ProPresetBar")).Visibility = Visibility.Collapsed;
            Render(window, Path.Combine(root,"english-basic.png"));
            panel.Visibility = Visibility.Collapsed;
            var pro = (TabControl)window.FindName("SettingsPanel"); pro.Visibility = Visibility.Visible;
            ((FrameworkElement)window.FindName("ProPresetBar")).Visibility = Visibility.Visible;
            for (var i=0;i<pro.Items.Count;i++) { pro.SelectedIndex=i; Render(window, Path.Combine(root,$"english-pro-{i}.png")); }
            Check(Settings.Load().Language == "en", "Language setting round trip");
            Console.WriteLine("PASS language picker: RU/EN/Auto/EN, unchanged capture settings, persistence, basic and every Pro tab rendered");
        } finally { window.Close(); app.Shutdown(); }
    }
    static void Render(Window window, string file) {
        var content = (FrameworkElement)window.Content;
        ((Grid)content).Background = window.Background;
        content.Measure(new Size(1180,900)); content.Arrange(new Rect(0,0,1180,900)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1180,900,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);png.Save(stream);
    }
}
