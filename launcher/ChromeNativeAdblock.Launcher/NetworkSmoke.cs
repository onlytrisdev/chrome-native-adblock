using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ChromeNativeAdblock.Launcher;

internal sealed record NetworkSmokeResult(
    bool Success,
    int BaselinePageRequests,
    int BaselineBlockedResourceRequests,
    int HookedPageRequests,
    int HookedBlockedResourceRequests,
    RemoteInjectionResult NetworkServiceInjection,
    InjectionSmokeResult BaselineProcess,
    InjectionSmokeResult HookedProcess);

internal static class NetworkSmoke
{
    internal static NetworkSmokeResult Run(string chromePath, string dllPath, string filterPath)
    {
        using var baselineServer = new LocalServer();
        var baseline = InjectionSmoke.Run(
            chromePath,
            dllPath,
            filterPath,
            installHook: false,
            extraChromeArguments: "--remote-debugging-port=0 --remote-allow-origins=*",
            afterHook: (profile, _) => Navigate(profile, baselineServer.PageUrl));

        using var hookedServer = new LocalServer();
        RemoteInjectionResult? networkServiceInjection = null;
        var hooked = InjectionSmoke.Run(
            chromePath,
            dllPath,
            filterPath,
            installHook: false,
            extraChromeArguments: "--remote-debugging-port=0 --remote-allow-origins=*",
            afterHook: (profile, browserProcessId) =>
            {
                var networkProcessId = ChromeProcesses.WaitForNetworkService(browserProcessId);
                networkServiceInjection = InjectionSmoke.InjectExisting(
                    networkProcessId, dllPath, filterPath, installHook: true);
                Navigate(profile, hookedServer.PageUrl);
            });

        if (networkServiceInjection is null)
        {
            throw new InvalidOperationException("Network service injection did not run.");
        }

        var success = baseline.Success && hooked.Success &&
                      baselineServer.PageRequests > 0 &&
                      baselineServer.BlockedResourceRequests > 0 &&
                      hookedServer.PageRequests > 0 &&
                      hookedServer.BlockedResourceRequests == 0;
        return new NetworkSmokeResult(
            success,
            baselineServer.PageRequests,
            baselineServer.BlockedResourceRequests,
            hookedServer.PageRequests,
            hookedServer.BlockedResourceRequests,
            networkServiceInjection,
            baseline,
            hooked);
    }

    private static void Navigate(string profilePath, string url)
    {
        var portFile = Path.Combine(profilePath, "DevToolsActivePort");
        for (var attempt = 0; attempt < 100 && !File.Exists(portFile); attempt++)
        {
            Thread.Sleep(50);
        }
        if (!File.Exists(portFile))
        {
            throw new TimeoutException("Chrome did not create DevToolsActivePort.");
        }

        var lines = File.ReadAllLines(portFile);
        if (lines.Length == 0 || !int.TryParse(lines[0], out var port))
        {
            throw new InvalidDataException("DevToolsActivePort does not contain a valid port.");
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var targetsJson = client.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
        using var targets = JsonDocument.Parse(targetsJson);
        var websocketUrl = targets.RootElement.EnumerateArray()
            .First(target => target.GetProperty("type").GetString() == "page")
            .GetProperty("webSocketDebuggerUrl")
            .GetString() ?? throw new InvalidDataException("DevTools target has no WebSocket URL.");

        using var socket = new ClientWebSocket();
        socket.ConnectAsync(new Uri(websocketUrl), CancellationToken.None).GetAwaiter().GetResult();
        var command = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = 1,
            method = "Page.navigate",
            @params = new { url }
        });
        socket.SendAsync(command, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(500);
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private int _pageRequests;
        private int _blockedResourceRequests;

        internal LocalServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            PageUrl = $"http://127.0.0.1:{endpoint.Port}/";
            _worker = Task.Run(ServeAsync);
        }

        internal string PageUrl { get; }
        internal int PageRequests => Volatile.Read(ref _pageRequests);
        internal int BlockedResourceRequests => Volatile.Read(ref _blockedResourceRequests);

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[16 * 1024];
                var used = 0;
                while (used < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used));
                    if (read == 0) break;
                    used += read;
                    if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var firstLine = Encoding.ASCII.GetString(buffer, 0, used).Split("\r\n", 2)[0];
                var path = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";

                byte[] body;
                string contentType;
                if (path.StartsWith("/blocked.png", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _blockedResourceRequests);
                    body = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                    contentType = "image/png";
                }
                else if (path == "/")
                {
                    Interlocked.Increment(ref _pageRequests);
                    body = Encoding.UTF8.GetBytes("<!doctype html><img src=\"/blocked.png\"><p>network smoke</p>");
                    contentType = "text/html; charset=utf-8";
                }
                else
                {
                    body = Array.Empty<byte>();
                    contentType = "text/plain";
                }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
