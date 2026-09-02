using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

public sealed class PatternScanTests
{
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Cargo.toml")) &&
                Directory.Exists(Path.Combine(current.FullName, "launcher")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string FindNativeDll()
    {
        var root = FindRepositoryRoot();
        string[] candidates =
        [
            Path.Combine(root, "target", "release", "chrome_native_adblock.dll"),
            Path.Combine(root, "target", "debug", "chrome_native_adblock.dll"),
            Path.Combine(root, "dist", "chrome_native_adblock.dll"),
            Path.Combine(AppContext.BaseDirectory, "chrome_native_adblock.dll"),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("chrome_native_adblock.dll was not found for tests.");
    }

    [Fact]
    public void TestFindChromeDll()
    {
        try
        {
            var chromeExe = ChromeInstallation.FindStableChrome();
            var chromeDll = PatternScanSmoke.FindChromeDll(chromeExe);
            Assert.True(File.Exists(chromeDll), $"chrome.dll should exist at {chromeDll}");
            Assert.EndsWith("chrome.dll", chromeDll, StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            // If Chrome is not installed on this machine, test is skipped
        }
    }

    [Fact]
    public void TestGetHookResolutionModeInitial()
    {
        var dllPath = FindNativeDll();
        using var engine = new NativeEngine(dllPath);
        var mode = engine.GetHookResolutionMode();
        Assert.Equal(HookResolutionMode.NotInstalled, mode);

        var (hookMode, startRva, cancelRva) = engine.GetHookInfo();
        Assert.Equal(HookResolutionMode.NotInstalled, hookMode);
        Assert.Equal((nuint)0, startRva);
        Assert.Equal((nuint)0, cancelRva);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TestScanRealChromeDllIfPresent()
    {
        string? chromeDll = null;
        try
        {
            var chromeExe = ChromeInstallation.FindStableChrome();
            chromeDll = PatternScanSmoke.FindChromeDll(chromeExe);
        }
        catch (FileNotFoundException)
        {
            string[] directCandidates =
            [
                @"C:\Program Files\Google\Chrome\Application\152.0.7977.65\chrome.dll",
                @"C:\Program Files (x86)\Google\Chrome\Application\152.0.7977.65\chrome.dll",
            ];
            foreach (var cand in directCandidates)
            {
                if (File.Exists(cand))
                {
                    chromeDll = cand;
                    break;
                }
            }
        }

        if (chromeDll != null && File.Exists(chromeDll))
        {
            var dllPath = FindNativeDll();
            using var engine = new NativeEngine(dllPath);
            var (startRva, cancelRva) = engine.ScanChromeDllFile(chromeDll);

            var is797776 = chromeDll.Contains("152.0.7977.76", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(is797776 ? (nuint)0x099D910 : (nuint)0x08BE450, startRva);
            Assert.Equal(is797776 ? (nuint)0x0A5AFD00 : (nuint)0x0A5A09C0, cancelRva);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TestPatternScanSmokeRunner()
    {
        string? chromeExe = null;
        try
        {
            chromeExe = ChromeInstallation.FindStableChrome();
        }
        catch (FileNotFoundException)
        {
            // Skip if Chrome is not installed
        }

        if (chromeExe != null && File.Exists(chromeExe))
        {
            var dllPath = FindNativeDll();
            var result = PatternScanSmoke.Run(chromeExe, dllPath);

            Assert.True(result.Success);
            if (result.ChromeVersion == "152.0.7977.76")
            {
                Assert.Equal("0x99D910", result.StartRva);
                Assert.Equal("0xA5AFD00", result.CancelRva);
            }
            else
            {
                Assert.Equal("0x8BE450", result.StartRva);
                Assert.Equal("0xA5A09C0", result.CancelRva);
            }
            Assert.Null(result.Error);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TestScanMultiVersionChromiumBuildsIfPresent()
    {
            (string Path, nuint ExpectedStart, nuint ExpectedCancel)[] builds =
        [
            (@"C:\Program Files\Google\Chrome\Application\152.0.7977.65\chrome.dll", 0x08BE450, 0x0A5A09C0),
            (@"C:\Program Files\Google\Chrome\Application\152.0.7977.76\chrome.dll", 0x099D910, 0x0A5AFD00),
            (@"C:\Program Files\CocCoc\Browser\Application\151.0.7922.176\browser.dll", 0x08BE1B0, 0x0AAF6950),
            (@"C:\Program Files\BraveSoftware\Brave-Browser\Application\152.1.94.117\chrome.dll", 0x0992CA0, 0x0B98EEC0),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ms-playwright\chromium-1223\chrome-win64\chrome.dll"), 0x09BE170, 0x09D53BD0),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".cache\puppeteer\chrome\win64-148.0.7778.97\chrome-win64\chrome.dll"), 0x09BE170, 0x09D53B40),
        ];

        var dllPath = FindNativeDll();
        using var engine = new NativeEngine(dllPath);

        foreach (var (path, expStart, expCancel) in builds)
        {
            if (File.Exists(path))
            {
                var (startRva, cancelRva) = engine.ScanChromeDllFile(path);
                Assert.Equal(expStart, startRva);
                Assert.Equal(expCancel, cancelRva);
            }
        }
    }

    [Fact]
    public void TestScanInvalidPeFileThrows()
    {
        var dllPath = FindNativeDll();
        using var engine = new NativeEngine(dllPath);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, [0x00, 0x01, 0x02, 0x03, 0x04]);
            Assert.Throws<InvalidOperationException>(() => engine.ScanChromeDllFile(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
