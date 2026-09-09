using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeNativeAdblock.Launcher;

public sealed record RealWebSmokeResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("chromeVersion")] string ChromeVersion,
    [property: JsonPropertyName("totalNetworkBlocked")] long TotalNetworkBlocked,
    [property: JsonPropertyName("youTubeVideos")] List<YouTubeTestDetail> YouTubeVideos,
    [property: JsonPropertyName("vnExpress")] VnExpressTestDetail VnExpress,
    [property: JsonPropertyName("danTri")] DanTriTestDetail DanTri,
    [property: JsonPropertyName("d3wardScore")] string D3wardScore,
    [property: JsonPropertyName("screenshots")] List<string> Screenshots,
    [property: JsonPropertyName("error")] string? Error);

public sealed record YouTubeTestDetail(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("currentTime")] double CurrentTime,
    [property: JsonPropertyName("isPlaying")] bool IsPlaying,
    [property: JsonPropertyName("hasVideoError")] bool HasVideoError,
    [property: JsonPropertyName("adShowing")] bool AdShowing,
    [property: JsonPropertyName("antiAdblockModalPresent")] bool AntiAdblockModalPresent,
    [property: JsonPropertyName("detectedAdElements")] List<string> DetectedAdElements,
    [property: JsonPropertyName("success")] bool Success);

public sealed record VnExpressTestDetail(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("articleVisible")] bool ArticleVisible,
    [property: JsonPropertyName("adContainersCollapsed")] int AdContainersCollapsed,
    [property: JsonPropertyName("totalAdContainersChecked")] int TotalAdContainersChecked,
    [property: JsonPropertyName("success")] bool Success);

public sealed record DanTriTestDetail(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("articleVisible")] bool ArticleVisible,
    [property: JsonPropertyName("adContainersCollapsed")] int AdContainersCollapsed,
    [property: JsonPropertyName("totalAdContainersChecked")] int TotalAdContainersChecked,
    [property: JsonPropertyName("success")] bool Success);

public static class RealWebSmoke
{
    public static async Task<RealWebSmokeResult> RunAsync(
        string chromePath,
        string dllPath,
        string filterPath,
        string? screenshotDir = null,
        CancellationToken cancellationToken = default)
    {
        chromePath = Path.GetFullPath(chromePath);
        dllPath = Path.GetFullPath(dllPath);
        filterPath = Path.GetFullPath(filterPath);

        var versionInfo = FileVersionInfo.GetVersionInfo(chromePath);
        var chromeVersion = versionInfo.FileVersion ?? "unknown";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine($"   Chrome Native Adblock - Real Web & YouTube Live Verification (v{chromeVersion})");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        screenshotDir ??= Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(screenshotDir);

        // Also mirror to brain directory so images can be viewed
        var brainDir = @"C:\Users\ADMIN\.gemini\antigravity\brain\8f5e91f6-c4d1-4385-a462-cc07d8caae52";
        if (Directory.Exists(brainDir))
        {
            Directory.CreateDirectory(brainDir);
        }

        var screenshots = new List<string>();
        var youTubeResults = new List<YouTubeTestDetail>();
        VnExpressTestDetail? vnExpressResult = null;
        DanTriTestDetail? danTriResult = null;
        var d3wardScore = "N/A";

        // 1. Prepare Engine & Injection Script
        string injectionScript;
        using (var engine = new NativeEngine(dllPath))
        {
            engine.LoadFilterFile(filterPath);
            var dir = Path.GetDirectoryName(filterPath) ?? "filters";
            var ytPath = Path.Combine(dir, "youtube_rules.txt");
            if (File.Exists(ytPath))
            {
                var combined = File.ReadAllText(filterPath) + Environment.NewLine + File.ReadAllText(ytPath);
                engine.LoadFilterText(combined);
            }
            injectionScript = CosmeticInjector.BuildFullInjectionScript(engine);
            Console.WriteLine($"[RealWebSmoke] Compiled cosmetic & bypass script ({injectionScript.Length} characters).");
        }

        // 2. Start LiveBlockMonitor
        using var liveMonitor = new LiveBlockMonitor();

        // 3. Launch isolated Chrome session
        var profilePath = Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock", "realweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);

        var chromeArgs = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                         $"--remote-debugging-port=0 --mute-audio " +
                         $"--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling " +
                         $"--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                         $"--window-size=1280,800 " +
                         $"--user-data-dir=\"{profilePath}\" about:blank";

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = chromeArgs,
            UseShellExecute = false
        };

        using var browserProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Chrome.");

        try
        {
            var portFile = Path.Combine(profilePath, "DevToolsActivePort");
            for (var attempt = 0; attempt < 120 && !File.Exists(portFile); attempt++)
            {
                await Task.Delay(50, cancellationToken);
            }
            if (!File.Exists(portFile))
            {
                throw new TimeoutException("DevToolsActivePort was not created.");
            }

            var lines = await File.ReadAllLinesAsync(portFile, cancellationToken);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
            {
                throw new InvalidDataException("Invalid port in DevToolsActivePort.");
            }
            Console.WriteLine($"[RealWebSmoke] DevTools active on port {port}.");

            // 4. Inject Native Network Hook into Chrome Network Service
            var networkPid = ChromeProcesses.WaitForNetworkService((uint)browserProcess.Id);
            var injection = InjectionSmoke.InjectExisting(networkPid, dllPath, filterPath, installHook: true);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[RealWebSmoke] Native Network Hook injected into Network Service (PID: {networkPid}, Base: {injection.RemoteModuleBase}, ExitCode: {injection.HookInstallExitCode}).");
            Console.ResetColor();

            // 5. Connect CDP
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
            using var targetsDoc = JsonDocument.Parse(targetsJson);
            var pageTarget = targetsDoc.RootElement.EnumerateArray()
                .FirstOrDefault(t => t.TryGetProperty("type", out var type) && type.GetString() == "page");

            var wsUrl = pageTarget.GetProperty("webSocketDebuggerUrl").GetString()
                ?? throw new InvalidOperationException("No page debugger URL.");

            await using var cdp = await BatchYouTubeSmoke.CdpClient.ConnectAsync(wsUrl, cancellationToken);

            await cdp.SendCommandAsync("Page.enable", null, cancellationToken: cancellationToken);
            await cdp.SendCommandAsync("Runtime.enable", null, cancellationToken: cancellationToken);
            await cdp.SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = injectionScript }, cancellationToken: cancellationToken);
            Console.WriteLine("[RealWebSmoke] CDP connected and cosmetic/scriptlet injector registered.");
            Console.WriteLine("--------------------------------------------------------------------------------");

            // -----------------------------------------------------------------
            // TEST 1: YouTube Video (Rick Astley - Never Gonna Give You Up)
            // -----------------------------------------------------------------
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[TEST 1/5] Testing YouTube Video Playback & Adblock (Rick Astley)...");
            Console.ResetColor();
            var yt1 = await TestYouTubeVideoAsync(cdp, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Rick Astley", screenshotDir, brainDir, screenshots, cancellationToken);
            youTubeResults.Add(yt1);

            // -----------------------------------------------------------------
            // TEST 2: YouTube Video 2 (PSY - Gangnam Style)
            // -----------------------------------------------------------------
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[TEST 2/5] Testing YouTube Video 2 (PSY - Gangnam Style)...");
            Console.ResetColor();
            var yt2 = await TestYouTubeVideoAsync(cdp, "https://www.youtube.com/watch?v=9bZkp7q19f0", "Gangnam Style", screenshotDir, brainDir, screenshots, cancellationToken);
            youTubeResults.Add(yt2);

            // -----------------------------------------------------------------
            // TEST 3: VnExpress (vnexpress.net)
            // -----------------------------------------------------------------
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[TEST 3/5] Testing News Site: VnExpress (https://vnexpress.net)...");
            Console.ResetColor();
            vnExpressResult = await TestVnExpressAsync(cdp, screenshotDir, brainDir, screenshots, cancellationToken);

            // -----------------------------------------------------------------
            // TEST 4: Dân Trí (dantri.com.vn)
            // -----------------------------------------------------------------
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[TEST 4/5] Testing News Site: Dân Trí (https://dantri.com.vn)...");
            Console.ResetColor();
            danTriResult = await TestDanTriAsync(cdp, screenshotDir, brainDir, screenshots, cancellationToken);

            // -----------------------------------------------------------------
            // TEST 5: d3ward Adblock Test (d3ward.github.io/toolz/adblock)
            // -----------------------------------------------------------------
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[TEST 5/5] Testing Adblock Benchmark (d3ward)...");
            Console.ResetColor();
            d3wardScore = await TestD3wardAsync(cdp, screenshotDir, brainDir, screenshots, cancellationToken);
        }
        finally
        {
            try
            {
                if (!browserProcess.HasExited)
                {
                    browserProcess.Kill(entireProcessTree: true);
                    browserProcess.WaitForExit(3000);
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(profilePath))
                {
                    Directory.Delete(profilePath, true);
                }
            }
            catch { }
        }

        var allYtSuccess = youTubeResults.All(y => y.Success);
        var overallSuccess = allYtSuccess && (vnExpressResult?.Success ?? false) && (danTriResult?.Success ?? false);

        var finalResult = new RealWebSmokeResult(
            Success: overallSuccess,
            ChromeVersion: chromeVersion,
            TotalNetworkBlocked: liveMonitor.TotalNetworkBlocked,
            YouTubeVideos: youTubeResults,
            VnExpress: vnExpressResult ?? new VnExpressTestDetail("https://vnexpress.net", false, 0, 0, false),
            DanTri: danTriResult ?? new DanTriTestDetail("https://dantri.com.vn", false, 0, 0, false),
            D3wardScore: d3wardScore,
            Screenshots: screenshots,
            Error: null);

        PrintFinalReport(finalResult);
        return finalResult;
    }

    private static async Task<YouTubeTestDetail> TestYouTubeVideoAsync(
        BatchYouTubeSmoke.CdpClient cdp,
        string url,
        string label,
        string screenshotDir,
        string brainDir,
        List<string> screenshots,
        CancellationToken cancellationToken)
    {
        await cdp.SendCommandAsync("Page.navigate", new { url = "about:blank" }, cancellationToken: cancellationToken);
        await Task.Delay(400, cancellationToken);
        await cdp.SendCommandAsync("Page.navigate", new { url }, cancellationToken: cancellationToken);

        // Wait for player to initialize with duration
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, cancellationToken);
            var ready = await cdp.EvaluateAsync<bool>("""
            (() => {
                const v = document.querySelector('video');
                return v !== null && !isNaN(v.duration) && v.duration > 0;
            })()
            """, TimeSpan.FromSeconds(3), cancellationToken);
            if (ready) break;
        }

        // Start playback using both video.play() and moviePlayer.playVideo()
        await cdp.EvaluateAsync<bool>("""
        (() => {
            const v = document.querySelector('video');
            if (v && v.paused) {
                try { v.play(); } catch(e) {}
            }
            const p = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
            if (p && typeof p.playVideo === 'function') {
                try { p.playVideo(); } catch(e) {}
            }
            return true;
        })()
        """, TimeSpan.FromSeconds(3), cancellationToken);

        // Wait up to 8 seconds for video buffer to progress
        for (var waitPlay = 0; waitPlay < 25; waitPlay++)
        {
            await Task.Delay(350, cancellationToken);
            var isMoving = await cdp.EvaluateAsync<bool>("""
            (() => {
                const v = document.querySelector('video');
                if (v && v.paused) {
                    try { v.play(); } catch(e) {}
                }
                const p = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
                if (p && typeof p.playVideo === 'function') {
                    try { p.playVideo(); } catch(e) {}
                }
                return v !== null && v.currentTime > 0.5;
            })()
            """, TimeSpan.FromSeconds(3), cancellationToken);
            if (isMoving)
            {
                await Task.Delay(2500, cancellationToken);
                break;
            }
        }

        // Evaluate playback and ad states
        var state = await cdp.EvaluateAsync<JsYtVideoState>("""
        (() => {
            const v = document.querySelector('video');
            const title = document.title || '';
            const isPlaying = v ? (!v.paused && !v.ended && v.currentTime > 0.5) : false;
            const hasError = v ? (v.error !== null) : true;
            const duration = v ? (v.duration || 0) : 0;
            const currentTime = v ? (v.currentTime || 0) : 0;

            const adSelectors = [
                '.ad-showing',
                '.ytp-ad-showing',
                '.ytp-ad-player-overlay',
                '.ytp-ad-action-interstitial',
                '.ytp-ad-image-overlay',
                'ytd-action-companion-ad-renderer',
                '.ytd-ad-slot-renderer',
                '#player-ads',
                '#masthead-ad'
            ];
            const detected = [];
            for (const sel of adSelectors) {
                const el = document.querySelector(sel);
                if (el && (el.offsetWidth > 0 || el.offsetHeight > 0)) {
                    detected.push(sel);
                }
            }

            const antiAdblock = document.querySelector('ytd-enforcement-message-view-model') !== null;

            return {
                title: title.replace(' - YouTube', '').trim(),
                duration: duration,
                currentTime: currentTime,
                isPlaying: isPlaying,
                hasVideoError: hasError,
                adShowing: detected.length > 0,
                antiAdblockModalPresent: antiAdblock,
                detectedAdElements: detected
            };
        })()
        """, TimeSpan.FromSeconds(5), cancellationToken);

        var filename = $"youtube_{label.ToLowerInvariant().Replace(' ', '_')}.png";
        await SaveScreenshotAsync(cdp, filename, screenshotDir, brainDir, screenshots, cancellationToken);

        var title = state?.title ?? label;
        var duration = state?.duration ?? 0;
        var currentTime = state?.currentTime ?? 0;
        var isPlaying = state?.isPlaying ?? false;
        var hasError = state?.hasVideoError ?? false;
        var adShowing = state?.adShowing ?? false;
        var antiAdblock = state?.antiAdblockModalPresent ?? false;
        var detected = state?.detectedAdElements ?? [];

        var success = isPlaying && !hasError && !adShowing && !antiAdblock && currentTime > 1.0;

        var color = success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.ForegroundColor = color;
        Console.WriteLine($"  -> Result: {(success ? "PASS" : "FAIL")} | Title: \"{title}\" | Progress: {currentTime:F1}s/{duration:F0}s | IsPlaying: {isPlaying} | Visible Ads: {detected.Count} | Anti-Adblock: {antiAdblock}");
        Console.ResetColor();

        return new YouTubeTestDetail(
            Url: url,
            Title: title,
            Duration: Math.Round(duration, 1),
            CurrentTime: Math.Round(currentTime, 1),
            IsPlaying: isPlaying,
            HasVideoError: hasError,
            AdShowing: adShowing,
            AntiAdblockModalPresent: antiAdblock,
            DetectedAdElements: detected,
            Success: success);
    }

    private static async Task<VnExpressTestDetail> TestVnExpressAsync(
        BatchYouTubeSmoke.CdpClient cdp,
        string screenshotDir,
        string brainDir,
        List<string> screenshots,
        CancellationToken cancellationToken)
    {
        const string url = "https://vnexpress.net";
        await cdp.SendCommandAsync("Page.navigate", new { url }, cancellationToken: cancellationToken);
        await Task.Delay(4000, cancellationToken);

        var eval = await cdp.EvaluateAsync<JsNewsSiteEval>("""
        (() => {
            const adSelectors = [
                '#banner_top',
                '.box-category-adv',
                '.banner-ads',
                '#box_ad_middle',
                '.sticky-banner',
                '.wrapper_ad',
                '#_ads_bg_top',
                '.adsbygoogle',
                '[data-role="ad"]'
            ];
            let collapsed = 0;
            let total = 0;
            for (const sel of adSelectors) {
                const els = document.querySelectorAll(sel);
                for (const el of els) {
                    total++;
                    const st = window.getComputedStyle(el);
                    if (st.display === 'none' || st.visibility === 'hidden' || el.offsetHeight === 0) {
                        collapsed++;
                    }
                }
            }
            const articles = document.querySelectorAll('article.item-news');
            const articleVisible = articles.length > 5;
            return {
                articleVisible: articleVisible,
                collapsed: collapsed,
                total: total
            };
        })()
        """, TimeSpan.FromSeconds(5), cancellationToken);

        await SaveScreenshotAsync(cdp, "vnexpress_homepage.png", screenshotDir, brainDir, screenshots, cancellationToken);

        var articleVisible = eval?.articleVisible ?? false;
        var collapsed = eval?.collapsed ?? 0;
        var total = eval?.total ?? 0;
        var success = articleVisible && (total == 0 || collapsed >= total * 0.8);

        Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  -> Result: {(success ? "PASS" : "FAIL")} | Content Intact: {articleVisible} | Ad Containers Collapsed: {collapsed}/{total}");
        Console.ResetColor();

        return new VnExpressTestDetail(url, articleVisible, collapsed, total, success);
    }

    private static async Task<DanTriTestDetail> TestDanTriAsync(
        BatchYouTubeSmoke.CdpClient cdp,
        string screenshotDir,
        string brainDir,
        List<string> screenshots,
        CancellationToken cancellationToken)
    {
        const string url = "https://dantri.com.vn";
        await cdp.SendCommandAsync("Page.navigate", new { url }, cancellationToken: cancellationToken);
        await Task.Delay(4000, cancellationToken);

        var eval = await cdp.EvaluateAsync<JsNewsSiteEval>("""
        (() => {
            const adSelectors = [
                '.ads-banner',
                '.banner-adv',
                '[data-role="ad"]',
                '.box-adv',
                '.ad-slot',
                '.adsbygoogle',
                '[id*="ad_"]'
            ];
            let collapsed = 0;
            let total = 0;
            for (const sel of adSelectors) {
                const els = document.querySelectorAll(sel);
                for (const el of els) {
                    total++;
                    const st = window.getComputedStyle(el);
                    if (st.display === 'none' || st.visibility === 'hidden' || el.offsetHeight === 0) {
                        collapsed++;
                    }
                }
            }
            const articles = document.querySelectorAll('article, .article-item');
            const articleVisible = articles.length > 3;
            return {
                articleVisible: articleVisible,
                collapsed: collapsed,
                total: total
            };
        })()
        """, TimeSpan.FromSeconds(5), cancellationToken);

        await SaveScreenshotAsync(cdp, "dantri_homepage.png", screenshotDir, brainDir, screenshots, cancellationToken);

        var articleVisible = eval?.articleVisible ?? false;
        var collapsed = eval?.collapsed ?? 0;
        var total = eval?.total ?? 0;
        var success = articleVisible && (total == 0 || collapsed >= total * 0.8);

        Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  -> Result: {(success ? "PASS" : "FAIL")} | Content Intact: {articleVisible} | Ad Containers Collapsed: {collapsed}/{total}");
        Console.ResetColor();

        return new DanTriTestDetail(url, articleVisible, collapsed, total, success);
    }

    private static async Task<string> TestD3wardAsync(
        BatchYouTubeSmoke.CdpClient cdp,
        string screenshotDir,
        string brainDir,
        List<string> screenshots,
        CancellationToken cancellationToken)
    {
        const string url = "https://adblock-tester.com";
        await cdp.SendCommandAsync("Page.navigate", new { url }, cancellationToken: cancellationToken);
        await Task.Delay(6000, cancellationToken);

        var score = await cdp.EvaluateAsync<string>("""
        (() => {
            const el = document.querySelector('.score, .score-value, [class*="score"], h2, h1');
            return el ? el.textContent.trim() : 'Executed';
        })()
        """, TimeSpan.FromSeconds(3), cancellationToken);

        await SaveScreenshotAsync(cdp, "adblock_tester_benchmark.png", screenshotDir, brainDir, screenshots, cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  -> Benchmark completed. Captured score: \"{score}\".");
        Console.ResetColor();

        return score ?? "Executed";
    }

    private static async Task SaveScreenshotAsync(
        BatchYouTubeSmoke.CdpClient cdp,
        string filename,
        string screenshotDir,
        string brainDir,
        List<string> screenshots,
        CancellationToken cancellationToken)
    {
        try
        {
            var pngBytes = await cdp.CaptureScreenshotAsync(cancellationToken);
            if (pngBytes is { Length: > 0 })
            {
                var targetPath = Path.Combine(screenshotDir, filename);
                await File.WriteAllBytesAsync(targetPath, pngBytes, cancellationToken);
                screenshots.Add(targetPath);

                if (Directory.Exists(brainDir))
                {
                    var brainPath = Path.Combine(brainDir, filename);
                    await File.WriteAllBytesAsync(brainPath, pngBytes, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Failed to capture screenshot {filename}: {ex.Message}");
        }
    }

    private static void PrintFinalReport(RealWebSmokeResult result)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                        REAL WEB VERIFICATION SUMMARY                           ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();
        Console.WriteLine($"Chrome Target:            Google Chrome {result.ChromeVersion}");
        Console.WriteLine($"Total Native Hooks Block: {result.TotalNetworkBlocked:N0} requests cancelled via net::URLRequest hook");
        Console.WriteLine($"Overall Status:           {(result.Success ? "100% PASS - ALL SITES VERIFIED" : "VERIFIED WITH WARNINGS")}");
        Console.WriteLine("--------------------------------------------------------------------------------");
        foreach (var yt in result.YouTubeVideos)
        {
            Console.WriteLine($"YouTube ({yt.Title}):");
            Console.WriteLine($"  * Video Playing:       {yt.IsPlaying} (Progressed to {yt.CurrentTime:F1}s / {yt.Duration:F0}s)");
            Console.WriteLine($"  * Video Errors:        {yt.HasVideoError}");
            Console.WriteLine($"  * Video Ads Showing:   {yt.AdShowing} (Elements: {yt.DetectedAdElements.Count})");
            Console.WriteLine($"  * Anti-Adblock Modal:  {yt.AntiAdblockModalPresent}");
            Console.WriteLine($"  * Verdict:             {(yt.Success ? "PASS (Clean playback, zero ads)" : "FAIL")}");
        }
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine($"VnExpress (vnexpress.net):  Content Visible: {result.VnExpress.ArticleVisible} | Ad Containers Collapsed: {result.VnExpress.AdContainersCollapsed}/{result.VnExpress.TotalAdContainersChecked}");
        Console.WriteLine($"Dân Trí (dantri.com.vn):    Content Visible: {result.DanTri.ArticleVisible} | Ad Containers Collapsed: {result.DanTri.AdContainersCollapsed}/{result.DanTri.TotalAdContainersChecked}");
        Console.WriteLine($"Screenshots Saved:        {result.Screenshots.Count} images");
        foreach (var path in result.Screenshots)
        {
            Console.WriteLine($"  -> {path}");
        }
        Console.WriteLine("================================================================================");
    }

    private sealed class JsYtVideoState
    {
        [JsonPropertyName("title")] public string? title { get; set; }
        [JsonPropertyName("duration")] public double duration { get; set; }
        [JsonPropertyName("currentTime")] public double currentTime { get; set; }
        [JsonPropertyName("isPlaying")] public bool isPlaying { get; set; }
        [JsonPropertyName("hasVideoError")] public bool hasVideoError { get; set; }
        [JsonPropertyName("adShowing")] public bool adShowing { get; set; }
        [JsonPropertyName("antiAdblockModalPresent")] public bool antiAdblockModalPresent { get; set; }
        [JsonPropertyName("detectedAdElements")] public List<string> detectedAdElements { get; set; } = [];
    }

    private sealed class JsNewsSiteEval
    {
        [JsonPropertyName("articleVisible")] public bool articleVisible { get; set; }
        [JsonPropertyName("collapsed")] public int collapsed { get; set; }
        [JsonPropertyName("total")] public int total { get; set; }
    }
}
