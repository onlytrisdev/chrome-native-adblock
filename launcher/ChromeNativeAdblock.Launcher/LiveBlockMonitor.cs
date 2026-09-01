using System.IO.Pipes;
using System.Text;

namespace ChromeNativeAdblock.Launcher;

public sealed class LiveBlockMonitor : IDisposable, IAsyncDisposable
{
    public const string PipeName = "ChromeNativeAdblock_Log";

    public string ActivePipeName { get; }

    private readonly Lock _consoleLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverLoopTask;
    private CosmeticInjector? _attachedInjector;

    private long _totalNetworkBlocked;
    private long _totalScriptletSanitizations;
    private long _totalAdSkips;
    private long _totalCosmeticInjections;

    public long TotalNetworkBlocked => Interlocked.Read(ref _totalNetworkBlocked);
    public long TotalScriptletSanitizations => Interlocked.Read(ref _totalScriptletSanitizations);
    public long TotalAdSkips => Interlocked.Read(ref _totalAdSkips);
    public long TotalCosmeticInjections => Interlocked.Read(ref _totalCosmeticInjections);

    public Action<string, string>? BlockCallback { get; set; }

    public LiveBlockMonitor(CosmeticInjector? cosmeticInjector = null, string pipeName = PipeName)
    {
        ActivePipeName = pipeName;
        if (cosmeticInjector != null)
        {
            Attach(cosmeticInjector);
        }

        _serverLoopTask = Task.Run(() => RunPipeServerLoopAsync(_cts.Token));
    }

    public void Attach(CosmeticInjector cosmeticInjector)
    {
        _attachedInjector = cosmeticInjector;
        cosmeticInjector.OnConsoleMessage += HandleConsoleMessage;
    }

    public void Detach()
    {
        if (_attachedInjector != null)
        {
            _attachedInjector.OnConsoleMessage -= HandleConsoleMessage;
            _attachedInjector = null;
        }
    }

    public void HandleConsoleMessage(string domain, string level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (text.Contains("[CNA-YT-SANITIZE]"))
        {
            RecordYouTubeSanitization();
        }
        else if (text.Contains("[CNA-YT-SKIP]"))
        {
            RecordYouTubeSkip();
        }
        else if (text.Contains("[CNA-CSS-HIDE]"))
        {
            var targetDomain = domain;
            const string prefix = "Injected CSS hide style on ";
            var idx = text.IndexOf(prefix, StringComparison.Ordinal);
            if (idx != -1)
            {
                var extracted = text.Substring(idx + prefix.Length).Trim();
                if (!string.IsNullOrEmpty(extracted))
                {
                    targetDomain = extracted;
                }
            }
            RecordCosmeticCss(targetDomain);
        }
    }

    public void RecordNetworkBlock(string url)
    {
        Interlocked.Increment(ref _totalNetworkBlocked);
        var time = DateTime.Now.ToString("HH:mm:ss");
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{time}] [NETWORK BLOCKED] {url}");
            Console.ResetColor();
        }
        BlockCallback?.Invoke("Network", url);
    }

    public void RecordYouTubeSanitization()
    {
        Interlocked.Increment(ref _totalScriptletSanitizations);
        var time = DateTime.Now.ToString("HH:mm:ss");
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{time}] [YOUTUBE SCRIPTLET] Sanitized playerResponse (stripped ad placements)");
            Console.ResetColor();
        }
        BlockCallback?.Invoke("YouTube", "Sanitized playerResponse (stripped ad placements)");
    }

    public void RecordYouTubeSkip()
    {
        Interlocked.Increment(ref _totalAdSkips);
        var time = DateTime.Now.ToString("HH:mm:ss");
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{time}] [YOUTUBE SKIP] Fast-forwarded video ad & clicked skip");
            Console.ResetColor();
        }
        BlockCallback?.Invoke("YouTube", "Fast-forwarded video ad & clicked skip");
    }

    public void RecordCosmeticCss(string domain)
    {
        Interlocked.Increment(ref _totalCosmeticInjections);
        var time = DateTime.Now.ToString("HH:mm:ss");
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[{time}] [COSMETIC CSS] Injected hide rules on {domain}");
            Console.ResetColor();
        }
        BlockCallback?.Invoke("Cosmetic", $"Injected hide rules on {domain}");
    }

    public void PrintSummary()
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("        Live Blocking Activity Summary            ");
            Console.WriteLine("==================================================");
            Console.WriteLine($"  - Total Network Requests Blocked  : {TotalNetworkBlocked}");
            Console.WriteLine($"  - Total YouTube Ads Stripped      : {TotalScriptletSanitizations}");
            Console.WriteLine($"  - Total YouTube Video Ads Skipped : {TotalAdSkips}");
            Console.WriteLine($"  - Total Cosmetic Hide Injections  : {TotalCosmeticInjections}");
            Console.WriteLine("==================================================");
            Console.ResetColor();
        }
    }

    private async Task RunPipeServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pipeServer = new NamedPipeServerStream(
                    ActivePipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await using (pipeServer.ConfigureAwait(false))
                {
                    await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ProcessClientAsync(pipeServer, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Delay before retrying pipe server
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!cancellationToken.IsCancellationRequested && stream.IsConnected)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                ProcessPipeLine(line);
            }
        }
    }

    private void ProcessPipeLine(string line)
    {
        if (line.StartsWith("NET_BLOCK|", StringComparison.OrdinalIgnoreCase))
        {
            var url = line.Substring("NET_BLOCK|".Length).Trim();
            RecordNetworkBlock(url);
        }
        else if (line.StartsWith("BLOCKED:", StringComparison.OrdinalIgnoreCase))
        {
            var url = line.Substring("BLOCKED:".Length).Trim();
            RecordNetworkBlock(url);
        }
    }

    public void Dispose()
    {
        Detach();
        _cts.Cancel();
        try
        {
            _serverLoopTask.Wait(500);
        }
        catch { }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        _cts.Cancel();
        try
        {
            await _serverLoopTask.ConfigureAwait(false);
        }
        catch { }
        _cts.Dispose();
    }
}
