using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

public sealed class Mv2GateLocatorTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void InstalledChromeHasSafeMv2PatchSetIfPresent()
    {
        ChromeInstallation? installation = null;
        try
        {
            installation = ChromeInstallationFinder.Find();
        }
        catch (Exception)
        {
            // If Chrome is not installed on this test runner, pass gracefully
            return;
        }

        if (installation == null || !File.Exists(installation.DllPath))
        {
            return;
        }

        var report = AnalysisService.Analyze(installation);
        Assert.True(report.Success, string.Join(Environment.NewLine, report.Diagnostics));
        Assert.NotNull(report.Target);
        Assert.Equal(0x7f, report.Target.ExpectedByte);
        Assert.Equal(0xeb, report.Target.ReplacementByte);
    }
}
