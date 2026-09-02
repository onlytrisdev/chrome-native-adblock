using System.IO.Pipes;
using System.Text;
using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

[CollectionDefinition("NamedPipeTests", DisableParallelization = true)]
public class NamedPipeCollectionDefinition { }

[Collection("NamedPipeTests")]
public sealed class LiveBlockMonitorTests
{
    [Fact]
    public async Task TestConsoleMessageHandling()
    {
        using var injector = new CosmeticInjector();
        using var monitor = new LiveBlockMonitor(injector);

        Assert.Equal(0, monitor.TotalNetworkBlocked);
        Assert.Equal(0, monitor.TotalScriptletSanitizations);
        Assert.Equal(0, monitor.TotalAdSkips);
        Assert.Equal(0, monitor.TotalCosmeticInjections);

        // Trigger YouTube sanitization
        monitor.HandleConsoleMessage("youtube.com", "info", "[CNA-YT-SANITIZE] Stripped adPlacements / playerAds from playerResponse");
        Assert.Equal(1, monitor.TotalScriptletSanitizations);

        // Trigger YouTube skip
        monitor.HandleConsoleMessage("youtube.com", "info", "[CNA-YT-SKIP] Fast-skipped video ad & clicked skip button");
        Assert.Equal(1, monitor.TotalAdSkips);

        // Trigger Cosmetic CSS injection
        monitor.HandleConsoleMessage("example.com", "info", "[CNA-CSS-HIDE] Injected CSS hide style on example.com");
        Assert.Equal(1, monitor.TotalCosmeticInjections);

        // Direct record network block
        monitor.RecordNetworkBlock("https://doubleclick.net/ad.js");
        Assert.Equal(1, monitor.TotalNetworkBlocked);

        monitor.PrintSummary();
    }

    [Fact]
    public async Task TestNamedPipeLogging()
    {
        const string testPipe = "ChromeNativeAdblock_Log_TestPipe";
        using var monitor = new LiveBlockMonitor(pipeName: testPipe);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.Delay(100, cts.Token);

        // Connect client and send NET_BLOCK message
        using (var pipeClient = new NamedPipeClientStream(".", testPipe, PipeDirection.Out))
        {
            await pipeClient.ConnectAsync(2000, cts.Token);
            using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync("NET_BLOCK|https://adservice.google.com/test_block");
        }

        // Wait for message to be processed
        for (int i = 0; i < 50; i++)
        {
            if (monitor.TotalNetworkBlocked >= 1) break;
            await Task.Delay(20, cts.Token);
        }
        Assert.Equal(1, monitor.TotalNetworkBlocked);
    }

    [Fact]
    public void TestScriptletLoggingSignatures()
    {
        var cssScript = CosmeticInjector.BuildCssInjectionScript(["#ad-banner", ".sidebar-ad"]);
        Assert.Contains("[CNA-CSS-HIDE]", cssScript);

        var ytScript = CosmeticInjector.BuildYouTubeBypassScript();
        Assert.DoesNotContain("[CNA-YT-SANITIZE]", ytScript);
        Assert.Contains("[CNA-YT-SKIP]", ytScript);
    }

    [Fact]
    public async Task TestNativeEngineLogNetworkBlock()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(root);
        string? dllPath = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "target", "release", "chrome_native_adblock.dll");
            if (File.Exists(candidate))
            {
                dllPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(dllPath);

        using var monitor = new LiveBlockMonitor();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(400, cts.Token);

        using (var engine = new NativeEngine(dllPath))
        {
            Assert.Contains("chrome-native-adblock", engine.GetVersion());
            engine.LogNetworkBlock("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js");
            var lastErr = engine.GetLastError();
            if (!string.IsNullOrEmpty(lastErr))
            {
                throw new InvalidOperationException($"Engine error: {lastErr}");
            }
        }

        for (int i = 0; i < 80; i++)
        {
            if (monitor.TotalNetworkBlocked >= 1) break;
            await Task.Delay(50, cts.Token);
        }

        Assert.Equal(1, monitor.TotalNetworkBlocked);
    }
}
