using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChromeNativeAdblock.Launcher;

internal sealed record VnExpressSmokeResult(
    bool Success,
    int TotalAdContainersTested,
    int CollapsedAdContainers,
    bool ArticleContentPreserved,
    bool CssCollapsingApplied,
    bool LayoutHealerApplied,
    string? Error);

internal static class VnExpressSmoke
{
    internal static VnExpressSmokeResult Run(string chromePath, string dllPath, string filterPath)
    {
        try
        {
            using var engine = new NativeEngine(dllPath);
            engine.LoadFilterFile(filterPath);

            var dir = Path.GetDirectoryName(filterPath) ?? "filters";
            var abpvnPath = Path.Combine(dir, "abpvn_basic.txt");
            if (File.Exists(abpvnPath))
            {
                var combined = File.ReadAllText(filterPath) + Environment.NewLine + File.ReadAllText(abpvnPath);
                engine.LoadFilterText(combined);
            }

            var script = CosmeticInjector.BuildFullInjectionScript(engine);
            var containsCss = script.Contains("cna-cosmetic-style");
            var containsLayoutHealer = script.Contains("healLayout");

            var cdpResult = VerifyVnExpressLayout(chromePath, script);

            var success = containsCss && containsLayoutHealer && cdpResult.Success;

            return new VnExpressSmokeResult(
                success,
                cdpResult.Total,
                cdpResult.Collapsed,
                cdpResult.ArticleVisible,
                containsCss,
                containsLayoutHealer,
                cdpResult.Error);
        }
        catch (Exception ex)
        {
            return new VnExpressSmokeResult(
                false,
                0,
                0,
                false,
                false,
                false,
                ex.Message);
        }
    }

    private sealed record LayoutEvaluation(
        bool Success,
        int Total,
        int Collapsed,
        bool ArticleVisible,
        string? Error);

    private static LayoutEvaluation VerifyVnExpressLayout(string chromePath, string injectionScript)
    {
        using var localServer = new VnExpressMockServer();
        var profilePath = Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock", "vnexpress-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);

        var arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                        $"--remote-debugging-port=0 --remote-allow-origins=* --user-data-dir=\"{profilePath}\" about:blank";
        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = arguments,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start headless Chrome.");

        try
        {
            var portFile = Path.Combine(profilePath, "DevToolsActivePort");
            for (var attempt = 0; attempt < 100 && !File.Exists(portFile); attempt++)
            {
                Thread.Sleep(50);
            }
            if (!File.Exists(portFile))
            {
                throw new TimeoutException("DevToolsActivePort not generated.");
            }

            var lines = File.ReadAllLines(portFile);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
            {
                throw new InvalidDataException("Invalid port in DevToolsActivePort.");
            }

            using var injector = new CosmeticInjector();
            var targets = injector.GetPageTargetsAsync(port).GetAwaiter().GetResult();
            var pageTarget = targets.FirstOrDefault(t => !string.IsNullOrEmpty(t.WebSocketDebuggerUrl));

            if (pageTarget?.WebSocketDebuggerUrl == null)
            {
                throw new InvalidOperationException("No suitable page target found.");
            }

            using var ws = new ClientWebSocket();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ws.ConnectAsync(new Uri(pageTarget.WebSocketDebuggerUrl), connectCts.Token).GetAwaiter().GetResult();

            // 1. Enable Page domain
            var enablePage = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Page.enable" });
            ws.SendAsync(enablePage, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            // 2. Add script to evaluate on new document
            var addScript = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 2,
                method = "Page.addScriptToEvaluateOnNewDocument",
                @params = new { source = injectionScript }
            });
            ws.SendAsync(addScript, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            // 3. Enable Runtime domain
            var enableRuntime = JsonSerializer.SerializeToUtf8Bytes(new { id = 3, method = "Runtime.enable" });
            ws.SendAsync(enableRuntime, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            // 4. Navigate to test page to trigger evaluation
            var navigate = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 4,
                method = "Page.navigate",
                @params = new { url = localServer.PageUrl }
            });
            ws.SendAsync(navigate, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            // Also evaluate immediately on current document
            var evalScript = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 5,
                method = "Runtime.evaluate",
                @params = new { expression = injectionScript }
            });
            ws.SendAsync(evalScript, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();


            // Wait for navigation & layout healer execution
            Thread.Sleep(800);

            // 5. Evaluate layout metrics
            const string evalJs = """
            (function() {
                const adSelectors = [
                    '#banner_top',
                    '.box-category-adv',
                    '.banner-ads',
                    '#box_ad_middle',
                    '.sticky-banner',
                    '.wrapper_ad',
                    '#_ads_bg_top'
                ];
                let collapsedCount = 0;
                let foundCount = 0;
                for (let i = 0; i < adSelectors.length; i++) {
                    const el = document.querySelector(adSelectors[i]);
                    if (el) {
                        foundCount++;
                        const style = window.getComputedStyle(el);
                        const isCollapsed = style.display === 'none' ||
                                            (el.offsetHeight === 0 && el.offsetWidth === 0) ||
                                            style.visibility === 'hidden';
                        if (isCollapsed) collapsedCount++;
                    }
                }
                const article = document.getElementById('main-article');
                const articleVisible = Boolean(article && window.getComputedStyle(article).display !== 'none' && article.offsetHeight > 0);
                const cssPresent = Boolean(document.getElementById('cna-cosmetic-style'));

                return {
                    total: adSelectors.length,
                    found: foundCount,
                    collapsed: collapsedCount,
                    articleVisible: articleVisible,
                    cssPresent: cssPresent,
                    url: location.href,
                    htmlLen: document.body ? document.body.innerHTML.length : 0,
                    success: (collapsedCount === adSelectors.length && articleVisible && cssPresent)
                };
            })()
            """;
            var checkLayout = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 100,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = evalJs,
                    returnByValue = true
                }
            });
            ws.SendAsync(checkLayout, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

            var buffer = new byte[65536];
            using var ms = new MemoryStream();
            var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!receiveCts.IsCancellationRequested)
            {
                try
                {
                    var result = ws.ReceiveAsync(buffer, receiveCts.Token).GetAwaiter().GetResult();
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var responseJson = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    if (responseJson.Contains("\"id\":100") || responseJson.Contains("\"id\": 100"))
                    {
                        using var doc = JsonDocument.Parse(responseJson);
                        if (doc.RootElement.TryGetProperty("result", out var resProp) &&
                            resProp.TryGetProperty("result", out var innerRes) &&
                            innerRes.TryGetProperty("value", out var valProp))
                        {
                            var total = valProp.TryGetProperty("total", out var tProp) ? tProp.GetInt32() : 0;
                            var found = valProp.TryGetProperty("found", out var fProp) ? fProp.GetInt32() : 0;
                            var collapsed = valProp.TryGetProperty("collapsed", out var cProp) ? cProp.GetInt32() : 0;
                            var articleVisible = valProp.TryGetProperty("articleVisible", out var aProp) && aProp.GetBoolean();
                            var success = valProp.TryGetProperty("success", out var sProp) && sProp.GetBoolean();
                            var url = valProp.TryGetProperty("url", out var uProp) ? uProp.GetString() : "unknown";
                            var htmlLen = valProp.TryGetProperty("htmlLen", out var hProp) ? hProp.GetInt32() : 0;

                            var diag = $"URL: {url}, HTML Length: {htmlLen}, Found: {found}/{total}, Collapsed: {collapsed}/{total}, ArticleVisible: {articleVisible}";
                            return new LayoutEvaluation(success, total, collapsed, articleVisible, success ? null : diag);
                        }
                    }
                }
                catch (Exception ex)
                {
                    return new LayoutEvaluation(false, 0, 0, false, ex.Message);
                }
            }

            return new LayoutEvaluation(false, 0, 0, false, "CDP response timeout.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(1000);
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
    }

    private sealed class VnExpressMockServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;

        internal VnExpressMockServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            PageUrl = $"http://127.0.0.1:{endpoint.Port}/";
            _worker = Task.Run(ServeAsync);
        }

        internal string PageUrl { get; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reqBuffer = new byte[16 * 1024];
                var used = 0;
                while (used < reqBuffer.Length)
                {
                    var read = await stream.ReadAsync(reqBuffer.AsMemory(used, reqBuffer.Length - used));
                    if (read == 0) break;
                    used += read;
                    if (Encoding.ASCII.GetString(reqBuffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }

                const string html = """
                <!doctype html>
                <html lang="vi">
                <head>
                    <meta charset="utf-8">
                    <title>VnExpress Test Page</title>
                </head>
                <body>
                    <div id="banner_top" style="min-height: 250px; height: 250px; margin: 15px 0;"></div>
                    <div class="box-category-adv" style="min-height: 600px; height: 600px; margin: 20px 0;">
                        <iframe src="about:blank"></iframe>
                    </div>
                    <div class="banner-ads" style="clear: both; display: flex; justify-content: center; min-height: 250px;">
                        <div id="google_ads_iframe_1"></div>
                    </div>
                    <div id="box_ad_middle" class="box-ad" style="min-height: 250px; margin-bottom: 20px;"></div>
                    <div class="sticky-banner" style="min-height: 90px;"></div>
                    <div class="wrapper_ad" style="height: 150px;">
                        <iframe src=""></iframe>
                    </div>
                    <div id="_ads_bg_top" class="lazier" style="min-height: 100px;"></div>
                    <div class="article-content" id="main-article" style="display: block; min-height: 300px; padding: 20px;">
                        <h1 class="title-news">Tin tức thời sự</h1>
                        <p class="description">Nội dung bài báo không bị ảnh hưởng bởi adblock.</p>
                        <p class="Normal">Đoạn văn tiếp theo hiển thị hoàn chỉnh và liền mạch.</p>
                    </div>
                </body>
                </html>
                """;
                var body = Encoding.UTF8.GetBytes(html);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _stop.Dispose();
        }
    }
}
