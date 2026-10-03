using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace S8Cam;

public enum HotkeyAction {
    TogglePrivacyMute,
    ToggleTorch,
    ZoomIn,
    ZoomOut,
    ToggleStream
}

public sealed class Hotkeys : IDisposable {
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly IntPtr windowHandle;
    private readonly HwndSource? hwndSource;
    private readonly Action<HotkeyAction> onAction;
    private readonly List<int> registeredIds = new();
    private bool disposed;

    public Hotkeys(IntPtr hWnd, Action<HotkeyAction> handler) {
        windowHandle = hWnd;
        onAction = handler;
        hwndSource = HwndSource.FromHwnd(hWnd);
        hwndSource?.AddHook(HwndHook);
    }

    public void RegisterDefaultHotkeys() {
        // User requested disabling all hotkeys
    }

    private void Register(int id, uint modifiers, uint vk, HotkeyAction action) {
        if (RegisterHotKey(windowHandle, id, modifiers, vk)) {
            registeredIds.Add(id);
            actionMap[id] = action;
        }
    }

    private readonly Dictionary<int, HotkeyAction> actionMap = new();

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_HOTKEY) {
            var id = wParam.ToInt32();
            if (actionMap.TryGetValue(id, out var action)) {
                onAction(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        foreach (var id in registeredIds) {
            UnregisterHotKey(windowHandle, id);
        }
        registeredIds.Clear();
        hwndSource?.RemoveHook(HwndHook);
    }
}
