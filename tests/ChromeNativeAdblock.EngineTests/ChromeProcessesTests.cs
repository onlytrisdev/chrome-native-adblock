using System.Diagnostics;
using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

public sealed class ChromeProcessesTests
{
    [Fact]
    public void TestProcessQueryMethodsDoNotThrow()
    {
        var currentPid = checked((uint)Process.GetCurrentProcess().Id);

        // Test IsAnyChromeRunning with non-existing pid
        var isRunning = ChromeProcesses.IsAnyChromeRunning(userDataDir: "C:\\NonExistentPath_Test", browserPid: 999999);
        Assert.False(isRunning);

        // Test GetRunningChromePids with non-existing user data dir
        var pids = ChromeProcesses.GetRunningChromePids(userDataDir: "C:\\NonExistentPath_Test");
        Assert.Empty(pids);

        // Test FindMainBrowserPid with non-existing user data dir
        var mainPid = ChromeProcesses.FindMainBrowserPid(userDataDir: "C:\\NonExistentPath_Test", fallbackPid: null);
        Assert.Null(mainPid);

        // Test FindNetworkServices with non-existing user data dir
        var networkPids = ChromeProcesses.FindNetworkServices(browserProcessId: 999999, userDataDir: "C:\\NonExistentPath_Test");
        Assert.Empty(networkPids);
    }
}
