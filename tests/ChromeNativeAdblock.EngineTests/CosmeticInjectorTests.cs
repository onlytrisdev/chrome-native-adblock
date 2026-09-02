using ChromeNativeAdblock.Launcher;
using Xunit;

namespace ChromeNativeAdblock.EngineTests;

public sealed class CosmeticInjectorTests
{
    [Fact]
    public void TestYouTubeBypassScriptUsesConservativeDomCleanupOnly()
    {
        var script = CosmeticInjector.BuildYouTubeBypassScript();

        // Keep cosmetic cleanup and a rate-limited player skip path.
        var expectedSelectors = new[]
        {
            ".ytp-ad-image-overlay",
            ".ytp-ad-overlay-image",
            "ytd-action-companion-ad-renderer",
            ".ytp-ad-overlay-container",
            "ytd-ad-slot-renderer",
            "#player-ads",
            "#masthead-ad",
            ".ytp-ad-skip-button-modern"
        };

        foreach (var selector in expectedSelectors)
        {
            Assert.Contains(selector, script);
        }

        // Check mutation observer
        Assert.Contains("MutationObserver", script);
        Assert.Contains("childList: true", script);
        Assert.Contains("subtree: true", script);
        Assert.Contains("player.skipAd()", script);
        Assert.Contains("lastPlayerSkipAttempt", script);

        // Never rewrite YouTube data/player APIs or seek the main timeline.
        Assert.DoesNotContain("window.fetch =", script);
        Assert.DoesNotContain("JSON.parse =", script);
        Assert.DoesNotContain("XMLHttpRequest.prototype", script);
        Assert.DoesNotContain("ytInitialPlayerResponse", script);
        Assert.DoesNotContain("player.seekTo", script);
        Assert.Contains("adVideo.currentTime = adVideo.duration", script);
        Assert.Contains("adVideo.playbackRate = 16", script);
        Assert.DoesNotContain("setInterval", script);
    }

    [Fact]
    public void TestFullInjectionScriptIncludesYouTubeAndGenericCosmetics()
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

        if (dllPath == null) return; // Skip if native dll not built in current test run environment

        using var engine = new NativeEngine(dllPath);
        var fullScript = CosmeticInjector.BuildFullInjectionScript(engine);

        Assert.Contains(".ytp-ad-image-overlay", fullScript);
        Assert.Contains("ytd-action-companion-ad-renderer", fullScript);
        Assert.Contains(".ytp-ad-skip-button-modern", fullScript);
        Assert.Contains("player.skipAd()", fullScript);
        Assert.DoesNotContain("[CNA-YT-SANITIZE]", fullScript);
    }

    [Fact]
    public void TestDeepContainerCollapserCssGenerated()
    {
        var cssScript = CosmeticInjector.BuildCssInjectionScript(["#custom-ad"]);

        Assert.Contains("#custom-ad", cssScript);
        Assert.Contains("display: none !important;", cssScript);
        Assert.Contains("min-height: 0 !important;", cssScript);
        Assert.Contains("height: 0 !important;", cssScript);
        Assert.Contains("max-height: 0 !important;", cssScript);
        Assert.Contains("margin: 0 !important;", cssScript);
        Assert.Contains("padding: 0 !important;", cssScript);
        Assert.Contains("border: none !important;", cssScript);
        Assert.Contains("visibility: hidden !important;", cssScript);
        Assert.Contains("pointer-events: none !important;", cssScript);
        Assert.Contains("iframe[src=\"about:blank\"]", cssScript);
        Assert.Contains("div[class*=\"box-category-adv\" i]", cssScript);
        Assert.Contains("div[class*=\"banner-ads\" i]", cssScript);
        Assert.Contains(".box-category-adv", cssScript);
        Assert.Contains("#banner_top", cssScript);
    }

    [Fact]
    public void TestSmartContainerCollapserScriptContainsLayoutHealingLogic()
    {
        var script = CosmeticInjector.BuildSmartContainerCollapserScript();

        Assert.Contains("function healLayout()", script);
        Assert.Contains("function isEffectivelyEmpty(", script);
        Assert.Contains("function collapseElement(", script);
        Assert.Contains("MutationObserver", script);
        Assert.Contains("DOMContentLoaded", script);
        Assert.Contains("setProperty('display', 'none', 'important')", script);
        Assert.Contains("setProperty('min-height', '0px', 'important')", script);
        Assert.Contains("iframe[src=\"about:blank\"]", script);
        Assert.Contains(".box-category-adv", script);
        Assert.Contains("#banner_top", script);
        Assert.Contains(".banner-ads", script);
        Assert.Contains("#box_ad_middle", script);
    }

    [Fact]
    public void TestFullInjectionScriptIncludesVietnameseNewsCosmeticSelectors()
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

        if (dllPath == null) return;

        using var engine = new NativeEngine(dllPath);
        var fullScript = CosmeticInjector.BuildFullInjectionScript(engine);

        var expectedVnSelectors = new[]
        {
            "#banner_top",
            ".box-category-adv",
            ".banner-center",
            "#box_ad_middle",
            ".box-ad",
            ".sticky-banner",
            ".inner-adv",
            ".zone-ad",
            ".wrapper_ad",
            ".ad_container",
            ".adv-box",
            ".block_ad",
            ".banner_ad",
            ".adv-sticky",
            ".ad-container",
            ".ads-sponsor",
            "#LeaderBoardTop",
            ".vmcadszone",
            ".asd-headt",
            "#Zingnews_SiteHeader",
            ".adv-24h-mid"
        };

        foreach (var selector in expectedVnSelectors)
        {
            Assert.Contains(selector, fullScript);
        }

        Assert.Contains("healLayout", fullScript);
        Assert.Contains("min-height: 0 !important;", fullScript);
    }

    [Fact]
    public void TestUrlScopedInjectionDoesNotMixYouTubeAndVietnameseRules()
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

        if (dllPath == null) return;

        using var engine = new NativeEngine(dllPath);
        engine.LoadFilterText("youtube.com##.youtube-only\nyoutube.com##+js(set-constant, cnaYoutubeMarker, true)\n24h.com.vn##.vn-only\nexample.org##.generic-ad\nexample.org##+js(set-constant, cnaGenericMarker, true)\n");

        var youtube = CosmeticInjector.BuildInjectionScriptForUrl(engine, "https://www.youtube.com/watch?v=test");
        Assert.Contains("__cnaYtSafeCleanupInstalled", youtube);
        Assert.Contains("player.skipAd()", youtube);
        Assert.DoesNotContain("cnaYoutubeMarker", youtube);
        Assert.Contains(".youtube-only", youtube);
        Assert.DoesNotContain(".vn-only", youtube);
        Assert.DoesNotContain(".adv-24h-mid", youtube);

        var vietnamese = CosmeticInjector.BuildInjectionScriptForUrl(engine, "https://www.24h.com.vn/news");
        Assert.Contains(".vn-only", vietnamese);
        Assert.Contains(".adv-24h-mid", vietnamese);
        Assert.DoesNotContain("__cnaYtSafeCleanupInstalled", vietnamese);
        Assert.DoesNotContain(".youtube-only", vietnamese);

        var unrelated = CosmeticInjector.BuildInjectionScriptForUrl(engine, "https://example.org/");
        Assert.Contains(".generic-ad", unrelated);
        Assert.Contains("cnaGenericMarker", unrelated);
        Assert.DoesNotContain("__cnaYtSafeCleanupInstalled", unrelated);
        Assert.DoesNotContain(".adv-24h-mid", unrelated);
    }

    [Fact]
    public void TestAbpvnBasicFilterFileExistsAndValid()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(root);
        string? abpvnFile = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "filters", "abpvn_basic.txt");
            if (File.Exists(candidate))
            {
                abpvnFile = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(abpvnFile);
        var content = File.ReadAllText(abpvnFile);

        var expectedDomains = new[]
        {
            "vnexpress.net",
            "dantri.com.vn",
            "tuoitre.vn",
            "kenh14.vn",
            "vietnamnet.vn",
            "thanhnien.vn",
            "znews.vn",
            "24h.com.vn",
            "eclick.vn",
            "admicro.vn"
        };

        foreach (var domain in expectedDomains)
        {
            Assert.Contains(domain, content);
        }
    }
}
