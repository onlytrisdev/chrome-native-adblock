using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeNativeAdblock.Launcher;

public sealed record YouTubeVideoSpec(
    int Index,
    string Url,
    string Category,
    string Description,
    bool IsLongVideo);

public sealed record YouTubeVideoTestResult(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("playerReady")] bool PlayerReady,
    [property: JsonPropertyName("playbackProgressed")] bool PlaybackProgressed,
    [property: JsonPropertyName("detectedAdElementsCount")] int DetectedAdElementsCount,
    [property: JsonPropertyName("initialAdPlacementsCount")] int InitialAdPlacementsCount,
    [property: JsonPropertyName("playerResponseAdPlacementsCount")] int PlayerResponseAdPlacementsCount,
    [property: JsonPropertyName("preRollClean")] bool PreRollClean,
    [property: JsonPropertyName("midRoll10Clean")] bool MidRoll10Clean,
    [property: JsonPropertyName("midRoll25Clean")] bool MidRoll25Clean,
    [property: JsonPropertyName("midRoll50Clean")] bool MidRoll50Clean,
    [property: JsonPropertyName("detectedAdSelectors")] List<string> DetectedAdSelectors,
    [property: JsonPropertyName("logs")] List<string> Logs);

public sealed record BatchYouTubeSmokeSummary(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("totalVideos")] int TotalVideos,
    [property: JsonPropertyName("passedCount")] int PassedCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("inconclusiveCount")] int InconclusiveCount,
    [property: JsonPropertyName("totalElapsedSeconds")] double TotalElapsedSeconds,
    [property: JsonPropertyName("chromeVersion")] string ChromeVersion,
    [property: JsonPropertyName("networkProcessId")] uint NetworkProcessId,
    [property: JsonPropertyName("results")] List<YouTubeVideoTestResult> Results);

public static class BatchYouTubeSmoke
{
    public static readonly IReadOnlyList<YouTubeVideoSpec> CuratedVideos = new List<YouTubeVideoSpec>
    {
        // 1-5: Music Videos & Hits
        new(1, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "Music Video", "Rick Astley - Never Gonna Give You Up", false),
        new(2, "https://www.youtube.com/watch?v=9bZkp7q19f0", "Music Video", "PSY - GANGNAM STYLE", false),
        new(3, "https://www.youtube.com/watch?v=OPf0YbXqDm0", "Music Video", "Mark Ronson - Uptown Funk ft. Bruno Mars", false),
        new(4, "https://www.youtube.com/watch?v=fJ9rUzIMcZQ", "Music Video", "Queen - Bohemian Rhapsody", false),
        new(5, "https://www.youtube.com/watch?v=kJQP7kiw5Fk", "Music Video", "Luis Fonsi - Despacito ft. Daddy Yankee", false),

        // 6-9: Long DJ Sets, Live Performances & 1-3 Hour Mixes
        new(6, "https://www.youtube.com/watch?v=c0-hvjV2A5Y", "DJ Mix / Long Set", "Fred again.. | Boiler Room London (1h 10m)", true),
        new(7, "https://www.youtube.com/watch?v=sCNlt5nvSI8", "DJ Mix / Long Set", "FKJ live at Salar de Uyuni for Cercle (1h 30m)", true),
        new(8, "https://www.youtube.com/watch?v=UedTcufyrHc", "Music Mix / Stream", "Synthwave 1 Hour Chill & Retrowave Mix", true),
        new(9, "https://www.youtube.com/watch?v=jfKfPfyJRdk", "Music Mix / Stream", "Lofi Girl - lofi hip hop radio - beats to study to", true),

        // 10-12: Long Podcasts & Discussions (1.5 - 2.5 Hours)
        new(10, "https://www.youtube.com/watch?v=jvqFAi7vkBc", "Podcast / Long Form", "Lex Fridman Podcast #367 - Sam Altman (2h 30m)", true),
        new(11, "https://www.youtube.com/watch?v=h2aWYj51rh8", "Podcast / Long Form", "Huberman Lab - Master Your Sleep & Alertness (2h 10m)", true),
        new(12, "https://www.youtube.com/watch?v=p33CS_o8XlQ", "Podcast / Long Form", "The Diary Of A CEO - Simon Sinek: Master Your Mind (1h 45m)", true),

        // 13-15: Gaming Streams, Longplays & Lore
        new(13, "https://www.youtube.com/watch?v=0e3GPea1Tyg", "Gaming / Stream", "Minecraft Speedrun World Record History (45m)", true),
        new(14, "https://www.youtube.com/watch?v=o1tBgzU4_1A", "Gaming / Lore", "Elden Ring Complete Story & Lore Explained (1h 20m)", true),
        new(15, "https://www.youtube.com/watch?v=QkkoHAzjnUs", "Gaming / Longplay", "GTA V Full Game Walkthrough Longplay (2h+)", true),

        // 16-20: Tech Reviews, Documentaries, Science & Trending
        new(16, "https://www.youtube.com/watch?v=e_kO04aWzls", "Tech Review", "MKBHD - Apple Vision Pro Review", false),
        new(17, "https://www.youtube.com/watch?v=3-5W8q_Yv1Y", "Science / Animation", "Kurzgesagt – The Last Human on Earth", false),
        new(18, "https://www.youtube.com/watch?v=094y1Z2wpJg", "Science / Math", "Veritasium - The Simplest Math Problem No One Can Solve", false),
        new(19, "https://www.youtube.com/watch?v=iG9CE55wbtY", "Trending / Talk", "Sir Ken Robinson - Do schools kill creativity? (TED)", false),
        new(20, "https://www.youtube.com/watch?v=xRPjKOmAxC4", "Live / Documentary", "NASA Live - Earth Views from International Space Station", true)
    };

    public static async Task<BatchYouTubeSmokeSummary> RunAsync(
        string chromePath,
        string dllPath,
        string filterPath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("     Chrome Native Adblock - 20 YouTube Videos Batch Adblock Verification       ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        chromePath = Path.GetFullPath(chromePath);
        dllPath = Path.GetFullPath(dllPath);
        filterPath = Path.GetFullPath(filterPath);

        if (!File.Exists(chromePath))
            throw new FileNotFoundException("Chrome executable not found.", chromePath);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("Native DLL not found.", dllPath);
        if (!File.Exists(filterPath))
            throw new FileNotFoundException("Filter list not found.", filterPath);

        var chromeVersion = FileVersionInfo.GetVersionInfo(chromePath).FileVersion ?? "unknown";
        Console.WriteLine($"[BatchYouTube] Target Chrome: {chromePath} (v{chromeVersion})");
        Console.WriteLine($"[BatchYouTube] Native DLL:    {dllPath}");
        Console.WriteLine($"[BatchYouTube] Filter Rules:  {filterPath}");

        // 1. Preload Native Engine & Build Script
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
            injectionScript = CosmeticInjector.BuildInjectionScriptForUrl(engine, "https://www.youtube.com/watch?v=cna-smoke");
            Console.WriteLine($"[BatchYouTube] Generated native cosmetic & bypass script ({injectionScript.Length} chars).");
        }

        // 2. Launch Chrome with isolated profile & DevTools port
        var profilePath = Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock", "yt-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);

        var chromeArgs = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                         $"--remote-debugging-port=0 --mute-audio " +
                         $"--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling " +
                         $"--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                         $"--user-data-dir=\"{profilePath}\" about:blank";

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = chromeArgs,
            UseShellExecute = false
        };

        using var browserProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Chrome browser process.");

        uint networkPid = 0;
        var testResults = new List<YouTubeVideoTestResult>();

        try
        {
            // Read active DevTools port
            var portFile = Path.Combine(profilePath, "DevToolsActivePort");
            for (var attempt = 0; attempt < 120 && !File.Exists(portFile); attempt++)
            {
                await Task.Delay(50, cancellationToken);
            }
            if (!File.Exists(portFile))
            {
                throw new TimeoutException("DevToolsActivePort was not created by Chrome.");
            }

            var lines = await File.ReadAllLinesAsync(portFile, cancellationToken);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
            {
                throw new InvalidDataException("Invalid port in DevToolsActivePort.");
            }
            Console.WriteLine($"[BatchYouTube] Chrome DevTools listening on port: {port}");

            // 3. Inject Native Network Hook into Chrome Network Service
            try
            {
                networkPid = ChromeProcesses.WaitForNetworkService((uint)browserProcess.Id);
                var hookResult = InjectionSmoke.InjectExisting(networkPid, dllPath, filterPath, installHook: true);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[BatchYouTube] Native Network Hook injected into Network Service (PID: {networkPid}, Base: {hookResult.RemoteModuleBase}).");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[BatchYouTube] Network service hook warning: {ex.Message}");
                Console.ResetColor();
            }

            // 4. Connect to Page Target over CDP
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var targetsJson = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellationToken);
            using var targetsDoc = JsonDocument.Parse(targetsJson);
            var pageTarget = targetsDoc.RootElement.EnumerateArray()
                .FirstOrDefault(t => t.TryGetProperty("type", out var type) && type.GetString() == "page");

            var wsUrl = pageTarget.GetProperty("webSocketDebuggerUrl").GetString()
                ?? throw new InvalidOperationException("No WebSocket debugger URL found for page target.");

            await using var cdp = await CdpClient.ConnectAsync(wsUrl, cancellationToken);

            // Enable domains and add injection script to evaluate on new document
            await cdp.SendCommandAsync("Page.enable", null, cancellationToken: cancellationToken);
            await cdp.SendCommandAsync("Runtime.enable", null, cancellationToken: cancellationToken);
            await cdp.SendCommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = injectionScript }, cancellationToken: cancellationToken);

            Console.WriteLine("[BatchYouTube] Cosmetic injection script registered for all navigations.");
            Console.WriteLine("--------------------------------------------------------------------------------");

            // 5. Run test loop across all 20 videos
            for (var i = 0; i < CuratedVideos.Count; i++)
            {
                var spec = CuratedVideos[i];
                var videoResult = await TestSingleVideoAsync(cdp, spec, cancellationToken);
                testResults.Add(videoResult);
            }
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

        stopwatch.Stop();
        var totalElapsed = stopwatch.Elapsed.TotalSeconds;
        var passed = testResults.Count(r => r.Verdict == "PASS");
        var failed = testResults.Count(r => r.Verdict == "FAIL");
        var inconclusive = testResults.Count(r => r.Verdict == "INCONCLUSIVE");
        var overallSuccess = passed == CuratedVideos.Count && failed == 0 && inconclusive == 0;

        var summary = new BatchYouTubeSmokeSummary(
            Success: overallSuccess,
            TotalVideos: CuratedVideos.Count,
            PassedCount: passed,
            FailedCount: failed,
            InconclusiveCount: inconclusive,
            TotalElapsedSeconds: Math.Round(totalElapsed, 2),
            ChromeVersion: chromeVersion,
            NetworkProcessId: networkPid,
            Results: testResults);

        // 6. Save results to JSON file
        var rootDir = FindRepositoryRoot();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var jsonText = JsonSerializer.Serialize(summary, jsonOptions);

        var outputPathRoot = Path.Combine(rootDir, "youtube_20_test_results.json");
        var outputPathLocal = Path.Combine(AppContext.BaseDirectory, "youtube_20_test_results.json");
        await File.WriteAllTextAsync(outputPathRoot, jsonText, cancellationToken);
        if (outputPathLocal != outputPathRoot)
        {
            try { await File.WriteAllTextAsync(outputPathLocal, jsonText, cancellationToken); } catch { }
        }

        // 7. Print summary report
        PrintSummaryReport(summary, outputPathRoot);

        return summary;
    }

    private static async Task<YouTubeVideoTestResult> TestSingleVideoAsync(
        CdpClient cdp,
        YouTubeVideoSpec spec,
        CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"[{spec.Index:D2}/20] Testing {spec.Category}: \"{spec.Description}\"... ");
        Console.ResetColor();

        var detectedSelectors = new List<string>();
        var title = spec.Description;
        double duration = 0;
        var preRollClean = false;
        var mid10Clean = false;
        var mid25Clean = false;
        var mid50Clean = false;
        var initialPlacements = 0;
        var playerPlacements = 0;
        var playerReady = false;
        var playbackProgressed = false;
        var seekAttempts = 0;
        var verifiedSeeks = 0;

        try
        {
            // 1. Navigate to YouTube Video
            await cdp.SendCommandAsync("Page.navigate", new { url = spec.Url }, cancellationToken: cancellationToken);

            // 2. Wait for player & video element to appear
            for (var wait = 0; wait < 40; wait++) // up to 10 seconds
            {
                await Task.Delay(250, cancellationToken);
                var checkState = await cdp.EvaluateAsync<JsPlayerState>("""
                (() => {
                    const video = document.querySelector('video');
                    const moviePlayer = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
                    return {
                        hasVideo: video !== null,
                        hasPlayer: moviePlayer !== null,
                        duration: video ? video.duration : 0,
                        title: document.title || ''
                    };
                })()
                """, TimeSpan.FromSeconds(3), cancellationToken);

                if (checkState is { hasVideo: true })
                {
                    playerReady = true;
                    if (!string.IsNullOrWhiteSpace(checkState.title) && checkState.title != "YouTube")
                    {
                        title = checkState.title.Replace(" - YouTube", "").Trim();
                    }
                    duration = checkState.duration;
                    break;
                }
            }

            if (!playerReady)
            {
                logs.Add("Player or video element did not initialize within timeout.");
            }

            var playbackStart = playerReady
                ? (await QueryAdStateAsync(cdp, cancellationToken)).CurrentTime
                : 0;

            // 3. Ensure video is playing and wait 2.5s for pre-roll ads
            await cdp.EvaluateAsync<bool>("""
            (() => {
                const video = document.querySelector('video');
                if (video && video.paused) {
                    try { video.play(); } catch(e) {}
                }
                return true;
            })()
            """, TimeSpan.FromSeconds(3), cancellationToken);

            await Task.Delay(2500, cancellationToken);

            // 4. Pre-roll check (0%)
            var preRollState = await QueryAdStateAsync(cdp, cancellationToken);
            playbackProgressed = playerReady && preRollState.CurrentTime > playbackStart + 0.25;
            if (playerReady && !playbackProgressed)
            {
                logs.Add("Playback did not make measurable progress during the observation window.");
            }
            initialPlacements = preRollState.InitialAdPlacements;
            playerPlacements = preRollState.PlayerAdPlacements;
            if (preRollState.Duration > 0) duration = preRollState.Duration;
            if (!string.IsNullOrWhiteSpace(preRollState.Title) && preRollState.Title != "YouTube")
            {
                title = preRollState.Title.Replace(" - YouTube", "").Trim();
            }

            preRollClean = !preRollState.IsAdShowing && preRollState.DetectedVisibleElements.Count == 0 &&
                           preRollState.InitialAdPlacements == 0 && preRollState.PlayerAdPlacements == 0;

            if (preRollState.DetectedVisibleElements.Count > 0)
            {
                detectedSelectors.AddRange(preRollState.DetectedVisibleElements);
            }

            // 5. Seek to 10% (Mid-roll check 1)
            if (duration > 10)
            {
                var target = duration * 0.10;
                seekAttempts++;
                var seekIssued = await SeekVideoAsync(cdp, target, cancellationToken);
                await Task.Delay(1500, cancellationToken);
                var mid10State = await QueryAdStateAsync(cdp, cancellationToken);
                if (seekIssued && IsSeekVerified(mid10State.CurrentTime, target, duration)) verifiedSeeks++;
                else logs.Add("10% seek could not be verified.");
                mid10Clean = !mid10State.IsAdShowing && mid10State.DetectedVisibleElements.Count == 0;
                if (mid10State.DetectedVisibleElements.Count > 0)
                {
                    detectedSelectors.AddRange(mid10State.DetectedVisibleElements);
                }
            }
            else
            {
                mid10Clean = true;
            }

            // 6. Seek to 25% (Mid-roll check 2)
            if (duration > 30)
            {
                var target = duration * 0.25;
                seekAttempts++;
                var seekIssued = await SeekVideoAsync(cdp, target, cancellationToken);
                await Task.Delay(1500, cancellationToken);
                var mid25State = await QueryAdStateAsync(cdp, cancellationToken);
                if (seekIssued && IsSeekVerified(mid25State.CurrentTime, target, duration)) verifiedSeeks++;
                else logs.Add("25% seek could not be verified.");
                mid25Clean = !mid25State.IsAdShowing && mid25State.DetectedVisibleElements.Count == 0;
                if (mid25State.DetectedVisibleElements.Count > 0)
                {
                    detectedSelectors.AddRange(mid25State.DetectedVisibleElements);
                }
            }
            else
            {
                mid25Clean = true;
            }

            // 7. Seek to 50% (Mid-roll check 3)
            if (duration > 60)
            {
                var target = duration * 0.50;
                seekAttempts++;
                var seekIssued = await SeekVideoAsync(cdp, target, cancellationToken);
                await Task.Delay(1500, cancellationToken);
                var mid50State = await QueryAdStateAsync(cdp, cancellationToken);
                if (seekIssued && IsSeekVerified(mid50State.CurrentTime, target, duration)) verifiedSeeks++;
                else logs.Add("50% seek could not be verified.");
                mid50Clean = !mid50State.IsAdShowing && mid50State.DetectedVisibleElements.Count == 0;
                if (mid50State.DetectedVisibleElements.Count > 0)
                {
                    detectedSelectors.AddRange(mid50State.DetectedVisibleElements);
                }
            }
            else
            {
                mid50Clean = true;
            }
        }
        catch (Exception ex)
        {
            logs.Add($"Exception during test: {ex.Message}");
        }

        var distinctDetected = detectedSelectors.Distinct().ToList();
        var adsObserved = distinctDetected.Count > 0 || initialPlacements > 0 || playerPlacements > 0 ||
                          !preRollClean || !mid10Clean || !mid25Clean || !mid50Clean;
        var evidenceComplete = playerReady && duration > 0 && playbackProgressed && verifiedSeeks == seekAttempts;
        var success = evidenceComplete && !adsObserved;
        var verdict = success ? "PASS" : adsObserved ? "FAIL" : "INCONCLUSIVE";

        if (success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASS (Duration: {Math.Round(duration)}s | Ads Detected: 0 | Placements: 0)");
            Console.ResetColor();
        }
        else if (verdict == "FAIL")
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL (Ads: {distinctDetected.Count}, InitPlacements: {initialPlacements}, PlayerPlacements: {playerPlacements})");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"INCONCLUSIVE (PlayerReady: {playerReady}, Duration: {Math.Round(duration)}s, PlaybackProgressed: {playbackProgressed}, Seeks: {verifiedSeeks}/{seekAttempts})");
            Console.ResetColor();
        }

        return new YouTubeVideoTestResult(
            Index: spec.Index,
            Url: spec.Url,
            Category: spec.Category,
            Description: spec.Description,
            Title: title,
            DurationSeconds: Math.Round(duration, 1),
            Verdict: verdict,
            Success: success,
            PlayerReady: playerReady,
            PlaybackProgressed: playbackProgressed,
            DetectedAdElementsCount: distinctDetected.Count,
            InitialAdPlacementsCount: initialPlacements,
            PlayerResponseAdPlacementsCount: playerPlacements,
            PreRollClean: preRollClean,
            MidRoll10Clean: mid10Clean,
            MidRoll25Clean: mid25Clean,
            MidRoll50Clean: mid50Clean,
            DetectedAdSelectors: distinctDetected,
            Logs: logs);
    }

    private static async Task<JsAdQueryState> QueryAdStateAsync(CdpClient cdp, CancellationToken cancellationToken)
    {
        var result = await cdp.EvaluateAsync<JsAdQueryState>("""
        (() => {
            const isAdShowing = document.querySelector('.ad-showing, .ytp-ad-showing, .ytp-ad-player-overlay, .ytp-ad-action-interstitial, .ytp-ad-image-overlay, ytd-action-companion-ad-renderer') !== null;
            const initialAdPlacements = (window.ytInitialPlayerResponse && window.ytInitialPlayerResponse.adPlacements) ? window.ytInitialPlayerResponse.adPlacements.length : 0;
            const moviePlayer = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
            const playerResponse = moviePlayer && typeof moviePlayer.getPlayerResponse === 'function' ? moviePlayer.getPlayerResponse() : null;
            const playerAdPlacements = (playerResponse && playerResponse.adPlacements) ? playerResponse.adPlacements.length : 0;
            const video = document.querySelector('video');
            const duration = video && !isNaN(video.duration) ? video.duration : 0;
            const currentTime = video && !isNaN(video.currentTime) ? video.currentTime : 0;
            const title = document.title || '';

            const adSelectors = [
                '.ad-showing',
                '.ytp-ad-showing',
                '.ytp-ad-player-overlay',
                '.ytp-ad-action-interstitial',
                '.ytp-ad-image-overlay',
                'ytd-action-companion-ad-renderer',
                '.ytd-ad-slot-renderer',
                'ytd-in-feed-ad-layout-renderer',
                '#player-ads',
                '#masthead-ad',
                '.ytp-ad-module',
                '.ytp-ad-overlay-container',
                '.ytp-ad-overlay-slot',
                '.ytp-ad-preview-container',
                '.ytp-ad-survey'
            ];
            const detectedVisible = [];
            for (let i = 0; i < adSelectors.length; i++) {
                const sel = adSelectors[i];
                const elements = document.querySelectorAll(sel);
                for (let j = 0; j < elements.length; j++) {
                    const el = elements[j];
                    const style = window.getComputedStyle(el);
                    const isVisible = style && style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0' && el.offsetHeight > 0;
                    if (isVisible) {
                        detectedVisible.push(sel);
                        break;
                    }
                }
            }

            return {
                isAdShowing: isAdShowing,
                initialAdPlacements: initialAdPlacements,
                playerAdPlacements: playerAdPlacements,
                duration: duration,
                currentTime: currentTime,
                title: title,
                detectedVisibleElements: detectedVisible
            };
        })()
        """, TimeSpan.FromSeconds(5), cancellationToken);

        return result ?? new JsAdQueryState();
    }

    private static bool IsSeekVerified(double currentTime, double targetSeconds, double duration)
    {
        var tolerance = Math.Max(5, duration * 0.03);
        return currentTime > 0 && Math.Abs(currentTime - targetSeconds) <= tolerance;
    }

    private static async Task<bool> SeekVideoAsync(CdpClient cdp, double targetSeconds, CancellationToken cancellationToken)
    {
        return await cdp.EvaluateAsync<bool>($$"""
        (() => {
            const player = document.getElementById('movie_player') || document.querySelector('.html5-video-player');
            if (player && typeof player.seekTo === 'function') {
                try { player.seekTo({{targetSeconds}}, true); return true; } catch(e) {}
            }
            const video = document.querySelector('video');
            if (video) {
                try { video.currentTime = {{targetSeconds}}; return true; } catch(e) {}
            }
            return false;
        })()
        """, TimeSpan.FromSeconds(3), cancellationToken);
    }

    private static void PrintSummaryReport(BatchYouTubeSmokeSummary summary, string outputPath)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("================================================================================");
        Console.WriteLine("                     YOUTUBE 20 VIDEO TEST SUMMARY REPORT                       ");
        Console.WriteLine("================================================================================");
        Console.ResetColor();

        Console.WriteLine($"Total Videos Tested : {summary.TotalVideos}");
        Console.ForegroundColor = summary.PassedCount == summary.TotalVideos ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"Passed (0 Ads)      : {summary.PassedCount} / {summary.TotalVideos} ({(summary.PassedCount * 100.0 / summary.TotalVideos):F1}%)");
        Console.ResetColor();
        if (summary.FailedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed              : {summary.FailedCount}");
            Console.ResetColor();
        }
        if (summary.InconclusiveCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Inconclusive        : {summary.InconclusiveCount}");
            Console.ResetColor();
        }
        Console.WriteLine($"Total Elapsed Time  : {summary.TotalElapsedSeconds:F1} seconds");
        Console.WriteLine($"Results Saved To    : {outputPath}");
        Console.WriteLine("--------------------------------------------------------------------------------");

        Console.WriteLine(string.Format("{0,-4} | {1,-20} | {2,-7} | {3,-10} | {4,-8} | {5}",
            "#", "Category", "Result", "Duration", "Ads Det.", "Title / Description"));
        Console.WriteLine(new string('-', 80));

        foreach (var item in summary.Results)
        {
            var status = item.Verdict;
            if (item.Success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
            }
            else if (item.Verdict == "FAIL")
            {
                Console.ForegroundColor = ConsoleColor.Red;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
            }

            var durStr = item.DurationSeconds > 0 ? $"{Math.Round(item.DurationSeconds)}s" : "Live/N/A";
            var shortTitle = item.Title.Length > 35 ? item.Title.Substring(0, 32) + "..." : item.Title;

            Console.WriteLine(string.Format("{0,-4} | {1,-20} | {2,-7} | {3,-10} | {4,-8} | {5}",
                item.Index, item.Category, status, durStr, item.DetectedAdElementsCount, shortTitle));
            Console.ResetColor();
        }
        Console.WriteLine("================================================================================");
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ChromeNativeAdblock.slnx")) ||
                File.Exists(Path.Combine(current, "Cargo.toml")) ||
                Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        return AppContext.BaseDirectory;
    }

    private sealed class JsPlayerState
    {
        [JsonPropertyName("hasVideo")] public bool hasVideo { get; set; }
        [JsonPropertyName("hasPlayer")] public bool hasPlayer { get; set; }
        [JsonPropertyName("duration")] public double duration { get; set; }
        [JsonPropertyName("title")] public string? title { get; set; }
    }

    private sealed class JsAdQueryState
    {
        [JsonPropertyName("isAdShowing")] public bool IsAdShowing { get; set; }
        [JsonPropertyName("initialAdPlacements")] public int InitialAdPlacements { get; set; }
        [JsonPropertyName("playerAdPlacements")] public int PlayerAdPlacements { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("currentTime")] public double CurrentTime { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("detectedVisibleElements")] public List<string> DetectedVisibleElements { get; set; } = [];
    }

    private sealed class CdpClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _nextId = 1;
        private readonly CancellationTokenSource _cts = new();
        private Task? _receiveTask;

        public static async Task<CdpClient> ConnectAsync(string wsUrl, CancellationToken cancellationToken = default)
        {
            var client = new CdpClient();
            await client._ws.ConnectAsync(new Uri(wsUrl), cancellationToken);
            client._receiveTask = Task.Run(client.ReceiveLoopAsync);
            return client;
        }

        public async Task<JsonElement> SendCommandAsync(
            string method,
            object? @params = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[id] = tcs;

            var payloadObj = @params == null ? (object)new { id, method } : new { id, method, @params };
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payloadObj);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            if (timeout.HasValue)
            {
                linkedCts.CancelAfter(timeout.Value);
            }

            await _sendLock.WaitAsync(linkedCts.Token);
            try
            {
                await _ws.SendAsync(payloadBytes, WebSocketMessageType.Text, true, linkedCts.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }

        public async Task<T?> EvaluateAsync<T>(
            string expression,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var res = await SendCommandAsync("Runtime.evaluate", new
                {
                    expression,
                    returnByValue = true,
                    awaitPromise = true
                }, timeout ?? TimeSpan.FromSeconds(5), cancellationToken);

                if (res.TryGetProperty("result", out var resultObj) &&
                    resultObj.TryGetProperty("value", out var val))
                {
                    var text = val.GetRawText();
                    return JsonSerializer.Deserialize<T>(text);
                }
            }
            catch { }
            return default;
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65536];
            using var ms = new MemoryStream();

            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var bytes = ms.ToArray();
                    ms.SetLength(0);

                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                    {
                        if (_pendingRequests.TryRemove(id, out var tcs))
                        {
                            if (doc.RootElement.TryGetProperty("result", out var resultProp))
                            {
                                tcs.TrySetResult(resultProp.Clone());
                            }
                            else if (doc.RootElement.TryGetProperty("error", out var errorProp))
                            {
                                tcs.TrySetException(new InvalidOperationException($"CDP Error: {errorProp.GetRawText()}"));
                            }
                            else
                            {
                                tcs.TrySetResult(doc.RootElement.Clone());
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }

            foreach (var (_, tcs) in _pendingRequests)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch { }
            try { _ws.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            _sendLock.Dispose();
        }
    }
}
