using System.IO;
using Microsoft.UI.Xaml;
using WinRT;

namespace ChromeNativeAdblock.Gui;

public static class Program
{
    [global::System.STAThreadAttribute]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogFatal("AppDomain.UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error"));
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogFatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            ComWrappersSupport.InitializeComWrappers();

            Application.Start((p) =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            LogFatal("Program.Main", ex);
        }
    }

    public static void LogFatal(string source, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gui_fatal.log");
            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FATAL in {source}:\n{ex}\nStackTrace:\n{ex.StackTrace}\n\n";
            File.AppendAllText(logPath, message);
            Console.Error.WriteLine(message);
        }
        catch
        {
            // Ignore logging errors
        }
    }
}
