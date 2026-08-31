using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChromeNativeAdblock.Launcher;

internal sealed record CosmeticSmokeResult(
    bool Success,
    string[] YouTubeSelectors,
    int InjectedScriptLength,
    bool ContainsYouTubeBypass,
    bool ContainsCssStyle,
    bool HeadlessCdpVerified,
    string? Error);

internal static class CosmeticSmoke
{
    internal static CosmeticSmokeResult Run(string chromePath, string dllPath, string filterPath)
    {
        try
        {
            using var engine = new NativeEngine(dllPath);
            engine.LoadFilterFile(filterPath);

            var dir = Path.GetDirectoryName(filterPath) ?? "filters";
            var ytPath = Path.Combine(dir, "youtube_rules.txt");
            if (File.Exists(ytPath))
            {
                var combined = File.ReadAllText(filterPath) + Environment.NewLine + File.ReadAllText(ytPath);
                engine.LoadFilterText(combined);
            }

            // 1. Check YouTube cosmetic rules
            var ytCosmetic = engine.GetUrlCosmeticResources("https://www.youtube.com/watch?v=smokeTest");
            var script = CosmeticInjector.BuildFullInjectionScript(engine);
            var containsBypass = script.Contains("sanitizePlayerResponse") && script.Contains("skipVideoAds");
            var containsCss = script.Contains("cna-cosmetic-style");
            var cdpVerified = false;
            try
            {
                cdpVerified = VerifyHeadlessCdp(chromePath, script);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[CosmeticSmoke] Headless CDP verification note: {ex.Message}");
                Console.ResetColor();
            }

            var success = ytCosmetic.HideSelectors.Length > 0 && containsBypass && containsCss && cdpVerified;

            return new CosmeticSmokeResult(
                success,
                ytCosmetic.HideSelectors,
                script.Length,
                containsBypass,
                containsCss,
                cdpVerified,
                null);
        }
        catch (Exception ex)
        {
            return new CosmeticSmokeResult(
                false,
                [],
                0,
                false,
                false,
                false,
                ex.Message);
        }
    }

    private static bool VerifyHeadlessCdp(string chromePath, string injectionScript)
    {
        using var localServer = new CosmeticTestServer();
        var profilePath = Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock", "cosmetic-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);

        var arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                        $"--remote-debugging-port=0 --remote-allow-origins=* --user-data-dir=\"{profilePath}\" {localServer.PageUrl}";

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

            // Wait for navigation
            Thread.Sleep(600);

            // 5. Evaluate to check if style was attached
            var checkStyle = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = 100,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = "Boolean(document.getElementById('cna-cosmetic-style'))",
                    returnByValue = true
                }
            });
            ws.SendAsync(checkStyle, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();

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
                        if (doc.RootElement.TryGetProperty("result", out var resObj) &&
                            resObj.TryGetProperty("result", out var innerRes) &&
                            innerRes.TryGetProperty("value", out var val) &&
                            val.GetBoolean())
                        {
                            return true;
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
            return false;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
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

    private sealed class CosmeticTestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;

        internal CosmeticTestServer()
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
                var html = "<!doctype html><html><head><title>Cosmetic Test</title></head><body><div class=\"ytd-ad-slot-renderer\">Ad Banner</div><div class=\"ad-banner\">Banner 2</div></body></html>";
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
