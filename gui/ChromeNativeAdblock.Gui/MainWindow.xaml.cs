using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace ChromeNativeAdblock.Gui;

public sealed partial class MainWindow : Window
{
    private AppWindow? _appWindow;
    private TrayIconManager? _trayIconManager;

    public static new MainWindow? Current { get; private set; }

    public MainWindow()
    {
        Current = this;
        try
        {
            InitializeComponent();
            ConfigureWindow();
            UpdateTitleBarLocalization();

            LocalizationService.LanguageChanged += UpdateTitleBarLocalization;

            RootFrame.Navigate(typeof(MainPage));
        }
        catch (Exception ex)
        {
            Program.LogFatal("MainWindow.Constructor", ex);
            throw;
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            Title = LocalizationService.Text("AppTitle");

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(CustomTitleBar);

            if (_appWindow != null)
            {
                _appWindow.Title = LocalizationService.Text("AppTitle");

                // Set window size (980x700)
                const int width = 980;
                const int height = 700;
                _appWindow.Resize(new SizeInt32(width, height));

                // Center window on current monitor
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredPosition = new PointInt32(
                        Math.Max(0, (displayArea.WorkArea.Width - width) / 2 + displayArea.WorkArea.X),
                        Math.Max(0, (displayArea.WorkArea.Height - height) / 2 + displayArea.WorkArea.Y)
                    );
                    _appWindow.Move(centeredPosition);
                }

                // Configure TitleBar styling for seamless dark/Mica look
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = _appWindow.TitleBar;
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    titleBar.ButtonForegroundColor = Colors.White;
                    titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(128, 255, 255, 255);
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(32, 255, 255, 255);
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(48, 255, 255, 255);
                    titleBar.ButtonPressedForegroundColor = Colors.White;
                }

                // Set icon if available
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }
            }

            // Initialize TrayIconManager
            _trayIconManager = new TrayIconManager(this, hwnd, _appWindow);
        }
        catch (Exception ex)
        {
            Program.LogFatal("MainWindow.ConfigureWindow", ex);
        }
    }

    public void UpdateTitleBarLocalization()
    {
        if (TitleText != null) TitleText.Text = LocalizationService.Text("AppTitle");
        if (SubtitleText != null) SubtitleText.Text = LocalizationService.Text("AppSubtitle");
        if (_appWindow != null) _appWindow.Title = LocalizationService.Text("AppTitle");
        _trayIconManager?.UpdateTooltip(LocalizationService.Text("TrayTooltip"));
    }

    public void MinimizeToTray()
    {
        _trayIconManager?.HideToTray(showBalloon: true);
    }

    public void RestoreFromTray()
    {
        _trayIconManager?.RestoreWindow();
    }
}
