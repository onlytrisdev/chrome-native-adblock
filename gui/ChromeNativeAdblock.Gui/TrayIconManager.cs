using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ChromeNativeAdblock.Gui;

internal sealed class TrayIconManager : IDisposable
{
    private const uint TRAY_ICON_ID = 1001;
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 1024;
    private const uint WM_DESTROY = 0x0002;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint TPM_LEFTBUTTON = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private readonly Window _mainWindow;
    private readonly IntPtr _hwnd;
    private readonly AppWindow? _appWindow;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SubclassProc _subclassProc;
    private IntPtr _hIcon;
    private bool _isDisposed;
    private bool _iconAdded;

    public TrayIconManager(Window mainWindow, IntPtr hwnd, AppWindow? appWindow)
    {
        _mainWindow = mainWindow;
        _hwnd = hwnd;
        _appWindow = appWindow;
        _dispatcherQueue = mainWindow.DispatcherQueue;

        _subclassProc = new SubclassProc(WndProc);
        SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101, IntPtr.Zero);

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                if (_hIcon == IntPtr.Zero)
                {
                    _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                }
            }

            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
            }

            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TRAY_ICON_ID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = LocalizationService.Text("TrayTooltip")
            };

            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
            if (_iconAdded)
            {
                nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref nid);
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal("TrayIconManager.InitializeTrayIcon", ex);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            var eventCode = (int)((long)lParam & 0xFFFF);
            var rawCode = (int)lParam;

            if (eventCode == WM_LBUTTONUP || eventCode == WM_LBUTTONDBLCLK ||
                rawCode == WM_LBUTTONUP || rawCode == WM_LBUTTONDBLCLK ||
                eventCode == NIN_SELECT || eventCode == NIN_KEYSELECT)
            {
                _dispatcherQueue.TryEnqueue(() => RestoreWindow());
                return IntPtr.Zero;
            }
            else if (eventCode == WM_RBUTTONUP || eventCode == WM_CONTEXTMENU ||
                     rawCode == WM_RBUTTONUP || rawCode == WM_CONTEXTMENU)
            {
                _dispatcherQueue.TryEnqueue(() => ShowContextMenu());
                return IntPtr.Zero;
            }
        }
        else if (uMsg == WM_DESTROY)
        {
            Dispose();
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void RestoreWindow()
    {
        try
        {
            if (IsIconic(_hwnd))
            {
                ShowWindow(_hwnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(_hwnd, SW_SHOW);
            }

            _appWindow?.Show();
            SetForegroundWindow(_hwnd);
            _mainWindow.Activate();
        }
        catch (Exception ex)
        {
            Program.LogFatal("TrayIconManager.RestoreWindow", ex);
        }
    }

    public void HideToTray(bool showBalloon = true)
    {
        try
        {
            ShowWindow(_hwnd, SW_HIDE);
            _appWindow?.Hide();

            if (showBalloon)
            {
                ShowNotification(
                    LocalizationService.Text("TrayNotificationTitle"),
                    LocalizationService.Text("TrayNotificationBody"));
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal("TrayIconManager.HideToTray", ex);
        }
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TRAY_ICON_ID,
                uFlags = NIF_INFO,
                szInfoTitle = title,
                szInfo = message,
                dwInfoFlags = NIIF_INFO,
                uTimeoutOrVersion = 3000
            };
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
        catch (Exception ex)
        {
            Program.LogFatal("TrayIconManager.ShowNotification", ex);
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        try
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TRAY_ICON_ID,
                uFlags = NIF_TIP,
                szTip = tooltip
            };
            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
        catch { }
    }

    private void ShowContextMenu()
    {
        try
        {
            GetCursorPos(out var pt);
            var hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            try
            {
                const uint CMD_OPEN = 1001;
                const uint CMD_LAUNCH = 1002;
                const uint CMD_STOP = 1003;
                const uint CMD_EXIT = 1004;

                var isRunning = MainPage.Current?.IsChromeRunning ?? false;

                var openText = LocalizationService.Text("TrayOpen");
                var launchText = LocalizationService.Text("TrayLaunch");
                var stopText = LocalizationService.Text("TrayStop");
                var exitText = LocalizationService.Text("TrayExit");

                AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_OPEN, openText);
                AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);

                if (isRunning)
                {
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_STOP, stopText);
                }
                else
                {
                    AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_LAUNCH, launchText);
                }

                AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)CMD_EXIT, exitText);

                SetForegroundWindow(_hwnd);
                var cmd = TrackPopupMenuEx(hMenu, TPM_LEFTBUTTON | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

                if (cmd == CMD_OPEN)
                {
                    RestoreWindow();
                }
                else if (cmd == CMD_LAUNCH)
                {
                    MainPage.Current?.LaunchChrome();
                }
                else if (cmd == CMD_STOP)
                {
                    MainPage.Current?.StopChrome();
                }
                else if (cmd == CMD_EXIT)
                {
                    Dispose();
                    Application.Current.Exit();
                    Environment.Exit(0);
                }
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }
        catch (Exception ex)
        {
            Program.LogFatal("TrayIconManager.ShowContextMenu", ex);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101);

            if (_iconAdded)
            {
                var nid = new NOTIFYICONDATAW
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwnd,
                    uID = TRAY_ICON_ID
                };
                Shell_NotifyIconW(NIM_DELETE, ref nid);
                _iconAdded = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
        catch { }
    }
}
