using Microsoft.UI.Xaml;

namespace ChromeNativeAdblock.Gui;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += App_UnhandledException;

        try
        {
            InitializeComponent();
            LocalizationService.Initialize();
        }
        catch (Exception ex)
        {
            Program.LogFatal("App.Constructor", ex);
            throw;
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Program.LogFatal("App.UnhandledException", e.Exception);
        // If we can handle it or report it without instant hard crash
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Program.LogFatal("App.OnLaunched", ex);
            throw;
        }
    }
}
