using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace S8Cam;

public sealed class TrayIcon : IDisposable {
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 101;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, IntPtr lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private readonly Window window;
    private readonly Action onOpen;
    private readonly Action onToggleStream;
    private readonly Action onExit;
    private readonly Action? onToggleMute;
    private readonly Action? onToggleHud;
    private readonly Action<string>? onApplyPreset;
    private readonly Action? onSnapshot;
    private readonly Action? onToggleRecord;
    private IntPtr hwnd;
    private bool added;
    private bool disposed;
    private HwndSource? hwndSource;
    private NOTIFYICONDATA nid;

    public TrayIcon(Window window, Action onOpen, Action onToggleStream, Action onExit, Action? onToggleMute = null, Action? onToggleHud = null, Action<string>? onApplyPreset = null, Action? onSnapshot = null, Action? onToggleRecord = null) {
        this.window = window;
        this.onOpen = onOpen;
        this.onToggleStream = onToggleStream;
        this.onExit = onExit;
        this.onToggleMute = onToggleMute;
        this.onToggleHud = onToggleHud;
        this.onApplyPreset = onApplyPreset;
        this.onSnapshot = onSnapshot;
        this.onToggleRecord = onToggleRecord;
    }

    public void Initialize() {
        var helper = new WindowInteropHelper(window);
        hwnd = helper.EnsureHandle();
        hwndSource = HwndSource.FromHwnd(hwnd);
        hwndSource?.AddHook(WndProc);

        IntPtr hIcon = IntPtr.Zero;
        try {
            var hModule = GetModuleHandle(null);
            if (hModule != IntPtr.Zero) {
                hIcon = LoadImage(hModule, (IntPtr)1, 1, 16, 16, 0);
                if (hIcon == IntPtr.Zero) hIcon = LoadIcon(hModule, (IntPtr)1);
            }
        } catch { }

        if (hIcon == IntPtr.Zero) {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath)) {
                hIcon = LoadImage(IntPtr.Zero, icoPath, 1, 16, 16, 0x00000010);
            }
        }
        if (hIcon == IntPtr.Zero) {
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION fallback
        }

        nid = new NOTIFYICONDATA {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "H3H Cam Receiver"
        };
        added = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    public void UpdateTip(string tip) {
        if (!added || disposed) return;
        nid.uFlags = NIF_TIP;
        nid.szTip = tip.Length > 120 ? tip[..120] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowBalloon(string title, string message) {
        if (!added || disposed) return;
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title.Length > 60 ? title[..60] : title;
        nid.szInfo = message.Length > 250 ? message[..250] : message;
        nid.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_TRAYICON) {
            var eventCode = lParam.ToInt32();
            if (eventCode is WM_LBUTTONDBLCLK or WM_LBUTTONUP) {
                onOpen();
                handled = true;
            } else if (eventCode == WM_RBUTTONUP) {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu() {
        var menu = new ContextMenu {
            Background = new SolidColorBrush(Color.FromRgb(12, 20, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 61, 86)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(231, 240, 255)),
            Padding = new Thickness(4)
        };

        MenuItem CreateItem(string header, Action onClick, bool isBold = false, string? iconText = null, Color? textColor = null) {
            var item = new MenuItem {
                Header = header,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(textColor ?? (isBold ? Color.FromRgb(112, 229, 195) : Color.FromRgb(231, 240, 255))),
                Padding = new Thickness(8, 6, 12, 6),
                FontSize = 13,
                Cursor = Cursors.Hand
            };
            if (iconText != null) {
                item.Icon = new TextBlock {
                    Text = iconText,
                    Foreground = new SolidColorBrush(textColor ?? Color.FromRgb(112, 229, 195)),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
            }
            item.Click += (_, _) => onClick();
            return item;
        }

        menu.Items.Add(CreateItem(L.Get("s_3b2bf9e7acbe"), onOpen, isBold: true, iconText: "🖥️"));
        menu.Items.Add(CreateItem(L.Get("s_b6892c770f04"), onToggleStream, iconText: "▶"));
        if (onSnapshot != null) {
            menu.Items.Add(CreateItem(L.Get("s_c615c4dcb15b"), onSnapshot, iconText: "📸"));
        }
        if (onToggleRecord != null) {
            menu.Items.Add(CreateItem(L.Get("s_17d4f3da4d5a"), onToggleRecord, iconText: "🔴"));
        }
        if (onToggleMute != null) {
            menu.Items.Add(CreateItem(L.Get("s_6e5c4067d45e"), onToggleMute, iconText: "🔒"));
        }
        if (onToggleHud != null) {
            menu.Items.Add(CreateItem(L.Get("s_0697cfc10f07"), onToggleHud, iconText: "📊"));
        }
        if (onApplyPreset != null) {
            var presetsMenu = new MenuItem {
                Header = L.Get("s_27c866501c07"),
                Foreground = new SolidColorBrush(Color.FromRgb(231, 240, 255)),
                Padding = new Thickness(8, 6, 12, 6),
                FontSize = 13
            };
            presetsMenu.Items.Add(CreateItem(L.Get("s_81972542b0e4"), () => onApplyPreset("streaming")));
            presetsMenu.Items.Add(CreateItem(L.Get("s_661451c0f114"), () => onApplyPreset("conference")));
            presetsMenu.Items.Add(CreateItem(L.Get("s_82d205c49029"), () => onApplyPreset("pro")));
            presetsMenu.Items.Add(CreateItem(L.Get("s_fbd463595a6a"), () => onApplyPreset("eco")));
            menu.Items.Add(presetsMenu);
        }
        menu.Items.Add(new Separator {
            Background = new SolidColorBrush(Color.FromRgb(29, 46, 68)),
            Height = 1,
            Margin = new Thickness(4, 3, 4, 3)
        });
        menu.Items.Add(CreateItem(L.Get("s_75cd24c315d4"), onExit, iconText: "✕", textColor: Color.FromRgb(255, 128, 128)));

        menu.IsOpen = true;
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (added) {
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            added = false;
        }
        if (nid.hIcon != IntPtr.Zero) {
            try { DestroyIcon(nid.hIcon); } catch { }
            nid.hIcon = IntPtr.Zero;
        }
        hwndSource?.RemoveHook(WndProc);
    }
}
