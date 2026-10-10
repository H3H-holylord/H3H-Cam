using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace S8Cam;

/// <summary>UI resources only. Camera/transport keys and numeric wire formats stay unchanged.</summary>
public static class L {
    private static readonly IReadOnlyDictionary<string, string> ru = Load("ru");
    private static readonly IReadOnlyDictionary<string, string> en = Load("en");
    private static readonly IReadOnlyDictionary<string, string> reverse = BuildReverse();
    private static string language = Resolve("auto");
    private static ResourceDictionary? activeResources;
    public static string Language => Volatile.Read(ref language);
    public static bool English => Language == "en";

    public static string Resolve(string? selection, CultureInfo? systemCulture = null) =>
        selection is "ru" or "en" ? selection :
        (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    public static void Configure(string? selection) {
        var selected = Resolve(selection);
        Volatile.Write(ref language, selected);
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.VerifyAccess();
        var next = new ResourceDictionary();
        foreach (var pair in Catalog(selected)) next[pair.Key] = pair.Value;
        app.Resources.MergedDictionaries.Add(next);
        if (activeResources != null) app.Resources.MergedDictionaries.Remove(activeResources);
        activeResources = next;
    }

    public static IReadOnlyDictionary<string, string> Catalog(string selected) => selected == "ru" ? ru : en;
    public static string Get(string key) => Catalog(Language).TryGetValue(key, out var value) ? value :
        ru.TryGetValue(key, out value) ? value : key;
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
    public static string Relocalize(string text) => reverse.TryGetValue(text, out var key) ? Get(key) : text;

    // These are human-readable fragments from Android, never used to change protocol fields.
    public static string PhoneText(string text) {
        if (!English || string.IsNullOrEmpty(text)) return text;
        return text.Replace("Фронтальная", "Front").Replace("Задняя", "Rear").Replace("Внешняя", "External")
            .Replace("логический", "logical").Replace("модуль", "module").Replace("Камера", "Camera")
            .Replace("Батарея", "Battery").Replace("Фокус", "Focus").Replace(" мм", " mm")
            .Replace("Готово", "Ready").Replace("Остановлено", "Stopped").Replace("Подключение", "Connecting")
            .Replace("Передача", "Streaming").Replace("Ожидание", "Waiting").Replace("Повтор", "Retry")
            .Replace("Захват", "Capture").Replace("на GPU телефона.", "on the phone GPU.")
            .Replace("Параметры обновлены на лету.", "Settings updated live.");
    }

    /// <summary>Refresh code-created static labels. DynamicResource bindings update themselves.</summary>
    public static void RefreshWindow(Window window) {
        var seen = new HashSet<DependencyObject>();
        void Update(DependencyObject node) {
            if (!seen.Add(node)) return;
            void Property(DependencyProperty property) {
                if (node.ReadLocalValue(property) is string original) {
                    var value = Relocalize(original);
                    if (value != original) node.SetCurrentValue(property, value);
                }
            }
            if (node is TextBlock) Property(TextBlock.TextProperty);
            if (node is ContentControl) Property(ContentControl.ContentProperty);
            if (node is HeaderedContentControl) Property(HeaderedContentControl.HeaderProperty);
            if (node is HeaderedItemsControl) Property(HeaderedItemsControl.HeaderProperty);
            if (node is FrameworkElement) Property(FrameworkElement.ToolTipProperty);
            if (node is Window) Property(Window.TitleProperty);
            foreach (var child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) Update(d);
            if (node is Visual or Visual3D) for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Update(VisualTreeHelper.GetChild(node, i));
            if (node is ComboBox combo && combo.ItemsSource != null) combo.Items.Refresh();
        }
        Update(window);
    }

    private static IReadOnlyDictionary<string, string> Load(string name) {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("S8Cam.Localization." + name + ".json")
            ?? throw new InvalidOperationException("Missing UI language resource: " + name);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Empty UI language resource: " + name);
    }
    private static IReadOnlyDictionary<string, string> BuildReverse() {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ru) values.TryAdd(pair.Value, pair.Key);
        foreach (var pair in en) values.TryAdd(pair.Value, pair.Key);
        return values;
    }
}
