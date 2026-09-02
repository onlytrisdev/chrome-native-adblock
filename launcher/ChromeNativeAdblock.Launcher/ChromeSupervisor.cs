using System.Diagnostics;
using System.Text;

namespace ChromeNativeAdblock.Launcher;

public sealed record ChromeSupervisorOptions(
    string? ChromePath = null,
    string? DllPath = null,
    string? FilterPath = null,
    string? UserDataDir = null,
    string[]? AdditionalArguments = null,
    bool EnableNativeAdblock = true,
    bool EnableMv2Enabler = true,
    bool AutoUpdateFilters = true,
    bool OpenExtensionsPage = false,
    int RemoteDebuggingPort = 0,
    bool Debug = false,
    Action<string>? LogCallback = null,
    Action<string, string>? BlockCallback = null,
    Action<uint>? StartedCallback = null);

public sealed class ChromeSupervisor : IDisposable
{
    private readonly ChromeSupervisorOptions _options;
    private readonly string _chromePath;
    private readonly string _dllPath;
    private readonly string _filterPath;
    private readonly CosmeticInjector _cosmeticInjector;
    private readonly LiveBlockMonitor? _liveBlockMonitor;
    private readonly HashSet<uint> _hookedNetworkProcesses = [];
    private readonly Lock _sync = new();
    private int? _activeDevToolsPort;

    public LiveBlockMonitor? Monitor => _liveBlockMonitor;

    public ChromeSupervisor(ChromeSupervisorOptions? options = null)
    {
        _options = options ?? new ChromeSupervisorOptions();
        _chromePath = _options.ChromePath ?? ChromeInstallation.FindStableChrome();

        var root = FindRepositoryRoot();
        _dllPath = _options.DllPath ?? (File.Exists(Path.Combine(root, "target", "release", "chrome_native_adblock.dll"))
            ? Path.Combine(root, "target", "release", "chrome_native_adblock.dll")
            : Path.Combine(AppContext.BaseDirectory, "chrome_native_adblock.dll"));

        var repositoryFiltersDir = Path.Combine(root, "filters");
        var defaultFiltersDir = Directory.Exists(repositoryFiltersDir)
            ? repositoryFiltersDir
            : Path.Combine(AppContext.BaseDirectory, "filters");
        _filterPath = _options.FilterPath ?? Path.Combine(defaultFiltersDir, "combined_rules.txt");

        _cosmeticInjector = new CosmeticInjector();

        // Enable real-time console & live block monitor if not disabled and native adblock is active
        var enableMonitor = _options.EnableNativeAdblock &&
            !string.Equals(Environment.GetEnvironmentVariable("CNA_DISABLE_MONITOR"), "1", StringComparison.OrdinalIgnoreCase);
        if (enableMonitor)
        {
            _liveBlockMonitor = new LiveBlockMonitor(_cosmeticInjector)
            {
                BlockCallback = _options.BlockCallback
            };
        }
    }

    private void Log(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_sync)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
        _options.LogCallback?.Invoke(message);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var title = (_options.EnableNativeAdblock, _options.EnableMv2Enabler) switch
        {
            (true, true) => "Chrome Native Adblock (Adblock + Manifest V2)",
            (true, false) => "Chrome Native Adblock (RAM Hook & YouTube Bypass)",
            (false, true) => "Chrome Manifest V2 Enabler (RAM Patch)",
            _ => "Chrome Launcher (Passthrough)"
        };

        Log("==================================================", ConsoleColor.Cyan);
        Log($"     {title}     ", ConsoleColor.Cyan);
        Log("==================================================", ConsoleColor.Cyan);

        if (_liveBlockMonitor != null)
        {
            Log("[Supervisor] Real-time block monitor active. Live block stream enabled.", ConsoleColor.Green);
        }

        // 1. Resolve Chrome installation
        ChromeInstallation? installation = null;
        try
        {
            installation = ChromeInstallationFinder.Find(_chromePath);
            Log($"[Supervisor] Chrome detected: {installation.Version} ({installation.ExecutablePath})", ConsoleColor.Gray);
        }
        catch (Exception ex)
        {
            Log($"[Supervisor] Warning resolving Chrome installation details: {ex.Message}", ConsoleColor.Yellow);
        }

        // 2. Prepare user data dir & Chrome launch arguments
        var effectivePort = Math.Max(0, _options.RemoteDebuggingPort);
        var userDataArgument = _options.AdditionalArguments?
            .FirstOrDefault(a => a.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase));
        var hasUserDataArg = userDataArgument != null;
        var effectiveUserDataDir = _options.UserDataDir;
        if (string.IsNullOrWhiteSpace(effectiveUserDataDir) && userDataArgument != null)
        {
            effectiveUserDataDir = userDataArgument[(userDataArgument.IndexOf('=') + 1)..].Trim().Trim('"');
        }
        if (string.IsNullOrWhiteSpace(effectiveUserDataDir) && !hasUserDataArg)
        {
            effectiveUserDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChromeNativeAdblock", "Profile");
            Directory.CreateDirectory(effectiveUserDataDir);
        }

        var chromeArgumentsList = new List<string>();
        if (_options.EnableNativeAdblock)
        {
            chromeArgumentsList.Add($"--remote-debugging-port={effectivePort}");
        }
        chromeArgumentsList.Add("--no-default-browser-check");
        chromeArgumentsList.Add("--no-first-run");

        if (!string.IsNullOrWhiteSpace(effectiveUserDataDir) && !hasUserDataArg)
        {
            chromeArgumentsList.Add($"--user-data-dir={Path.GetFullPath(effectiveUserDataDir)}");
        }

        if (_options.AdditionalArguments is { Length: > 0 })
        {
            chromeArgumentsList.AddRange(_options.AdditionalArguments);
        }

        if (_options.OpenExtensionsPage)
        {
            chromeArgumentsList.Add("chrome://extensions");
        }

        // 3. If Manifest V2 Enabler is active, repair profile preferences and locate RAM patch target
        PatchTarget? mv2PatchTarget = null;
        if (_options.EnableMv2Enabler)
        {
            if (installation == null)
            {
                installation = ChromeInstallationFinder.Find(_chromePath);
            }

            Log("[MV2] Scanning and repairing extension disabled states in profile Preferences...", ConsoleColor.Cyan);
            try
            {
                var repairResult = ChromeProfileRepair.RepairBeforeLaunch(installation, chromeArgumentsList);
                if (repairResult.ExtensionsReEnabled > 0)
                {
                    Log($"[MV2] Repaired {repairResult.ExtensionsReEnabled} disabled extension(s) across {repairResult.ProfilesChanged} profile(s).", ConsoleColor.Green);
                }
                else
                {
                    Log("[MV2] Profiles verified. All MV2 extensions in active state.", ConsoleColor.Gray);
                }
            }
            catch (Exception ex)
            {
                Log($"[MV2] Warning during profile repair: {ex.Message}", ConsoleColor.Yellow);
            }

            Log("[MV2] Analyzing chrome.dll for Manifest V2 Gate in RAM...", ConsoleColor.Cyan);
            var peImage = PeImage.Load(installation.DllPath);
            var locator = Mv2GateLocator.Locate(peImage);
            if (!locator.Success || locator.Target == null)
            {
                var errorMsg = $"MV2 Gate Locator failed: {string.Join("; ", locator.Diagnostics)}";
                Log($"[MV2] Error: {errorMsg}", ConsoleColor.Red);
                throw new InvalidOperationException(errorMsg);
            }

            mv2PatchTarget = locator.Target;
            Log($"[MV2] Located patch target ({mv2PatchTarget.RuleId}) at RVA 0x{mv2PatchTarget.PatchRva:X}.", ConsoleColor.Green);
        }

        // 4. If Native Adblock is active, ensure filters and preload cosmetic scripts
        NativeEngine? cosmeticEngine = null;
        Func<string, string> injectionScriptFactory = _ => string.Empty;
        if (_options.EnableNativeAdblock)
        {
            if (_options.AutoUpdateFilters)
            {
                try
                {
                    var filtersDir = Path.GetDirectoryName(_filterPath) ?? "filters";
                    var filterManager = new FilterManager(filtersDir);
                    Log("[Supervisor] Checking filter subscriptions...", ConsoleColor.Gray);
                    await filterManager.EnsureFiltersReadyAsync();
                    Log($"[Supervisor] Filters ready at: {_filterPath}", ConsoleColor.Gray);
                }
                catch (Exception ex)
                {
                    Log($"[Supervisor] Warning updating filters: {ex.Message}. Continuing with existing filters.", ConsoleColor.Yellow);
                }
            }

            if (!File.Exists(_dllPath))
            {
                throw new FileNotFoundException("Native DLL was not found. Please build the Rust crate first.", _dllPath);
            }
            if (!File.Exists(_filterPath))
            {
                throw new FileNotFoundException("Filter list was not found.", _filterPath);
            }

            cosmeticEngine = new NativeEngine(_dllPath);
            Log($"[Supervisor] Native Engine Version: {cosmeticEngine.GetVersion()}", ConsoleColor.Gray);
            cosmeticEngine.LoadFilterFile(_filterPath);
            injectionScriptFactory = url => CosmeticInjector.BuildInjectionScriptForUrl(cosmeticEngine, url);
            Log("[Supervisor] Cosmetic payloads will be generated from the exact URL of each tab.", ConsoleColor.Gray);
        }

        // 5. Launch Chrome process
        var launchStartedUtc = DateTime.UtcNow;
        uint currentBrowserPid = 0;
        Process? browserProcess = null;

        if (_options.EnableMv2Enabler && mv2PatchTarget != null && installation != null)
        {
            Log($"[MV2] Launching Chrome with RAM patch interception: {installation.ExecutablePath}", ConsoleColor.Cyan);
            var launchResult = ChromeDebugLauncher.Launch(
                installation,
                mv2PatchTarget,
                chromeArgumentsList,
                TimeSpan.FromSeconds(30));

            currentBrowserPid = launchResult.ProcessId;
            Log($"[MV2] RAM patch successfully applied to chrome.dll in process PID: {launchResult.ProcessId} (Address: {launchResult.RemoteAddress}).", ConsoleColor.Green);
        }
        else
        {
            var argsString = string.Join(' ', chromeArgumentsList.Select(ChromeDebugLauncher.QuoteArgument));
            Log($"[Supervisor] Launching Chrome: {_chromePath} {argsString}", ConsoleColor.Cyan);

            var startInfo = new ProcessStartInfo
            {
                FileName = _chromePath,
                Arguments = argsString,
                UseShellExecute = false
            };

            browserProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Chrome browser process.");

            currentBrowserPid = checked((uint)browserProcess.Id);
            Log($"[Supervisor] Chrome browser started with PID: {currentBrowserPid}", ConsoleColor.Green);
        }
        _options.StartedCallback?.Invoke(currentBrowserPid);


        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 6. Background tasks for Native Adblock
        Task networkHookTask = Task.CompletedTask;
        Task cosmeticTask = Task.CompletedTask;

        if (_options.EnableNativeAdblock)
        {
            networkHookTask = Task.Run(() => MonitorNetworkServiceLoopAsync(() => currentBrowserPid, effectiveUserDataDir, cts.Token), cts.Token);
            cosmeticTask = Task.Run(() => MonitorDevToolsAndCosmeticsLoopAsync(effectiveUserDataDir, effectivePort, launchStartedUtc, injectionScriptFactory, cts.Token), cts.Token);
        }

        // 7. Supervise session lifetime
        try
        {
            Log("[Supervisor] Active monitoring engaged. (Close Chrome or press Ctrl+C to terminate)", ConsoleColor.Cyan);

            var missedChecks = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                bool initialRunning = false;
                if (browserProcess != null)
                {
                    try { initialRunning = !browserProcess.HasExited; } catch { }
                }
                else
                {
                    try
                    {
                        using var proc = Process.GetProcessById(checked((int)currentBrowserPid));
                        initialRunning = !proc.HasExited;
                    }
                    catch { }
                }

                var runningPids = ChromeProcesses.GetRunningChromePids(effectiveUserDataDir);
                if (runningPids.Count > 0)
                {
                    var mainPid = ChromeProcesses.FindMainBrowserPid(effectiveUserDataDir, currentBrowserPid);
                    if (mainPid.HasValue && mainPid.Value != currentBrowserPid)
                    {
                        currentBrowserPid = mainPid.Value;
                        if (_options.Debug)
                        {
                            Log($"[Supervisor] Updated active browser PID: {currentBrowserPid}", ConsoleColor.DarkGray);
                        }
                    }
                    missedChecks = 0;
                }
                else if (initialRunning)
                {
                    missedChecks = 0;
                }
                else
                {
                    var cdpActive = false;
                    if (_options.EnableNativeAdblock)
                    {
                        if (_activeDevToolsPort.HasValue)
                        {
                            cdpActive = await IsCdpAliveAsync(_activeDevToolsPort.Value, cts.Token);
                        }
                        if (!cdpActive)
                        {
                            cdpActive = await IsCdpAliveAsync(effectivePort, cts.Token);
                        }
                    }

                    if (cdpActive)
                    {
                        missedChecks = 0;
                    }
                    else
                    {
                        missedChecks++;
                        if (missedChecks >= 4)
                        {
                            Log("[Supervisor] All Chrome processes have exited.", ConsoleColor.Yellow);
                            break;
                        }
                    }
                }

                try
                {
                    await Task.Delay(500, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _liveBlockMonitor?.PrintSummary();
            return 0;
        }
        catch (OperationCanceledException)
        {
            Log("[Supervisor] Termination requested.", ConsoleColor.Yellow);
            KillAllChromeInProfile(effectiveUserDataDir, browserProcess);
            _liveBlockMonitor?.PrintSummary();
            return 0;
        }
        finally
        {
            cts.Cancel();
            try { await Task.WhenAll(networkHookTask, cosmeticTask); } catch { }
            browserProcess?.Dispose();
            cosmeticEngine?.Dispose();
        }
    }

    private static void KillAllChromeInProfile(string? userDataDir, Process? initialProcess)
    {
        try
        {
            if (initialProcess is { HasExited: false })
            {
                initialProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(userDataDir))
        {
            var pids = ChromeProcesses.GetRunningChromePids(userDataDir);
            foreach (var pid in pids)
            {
                try
                {
                    using var proc = Process.GetProcessById(checked((int)pid));
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }
            }
        }
    }

    private static async Task<bool> IsCdpAliveAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            var res = await http.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task MonitorNetworkServiceLoopAsync(Func<uint> getBrowserPid, string? userDataDir, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var browserPid = getBrowserPid();
                var networkPids = ChromeProcesses.FindNetworkServices(browserPid, userDataDir);
                foreach (var networkPid in networkPids)
                {
                    bool isNew;
                    lock (_sync)
                    {
                        isNew = _hookedNetworkProcesses.Add(networkPid);
                    }

                    if (isNew)
                    {
                        Log($"[Supervisor] Detected Network Service process (PID: {networkPid}). Injecting native hook...", ConsoleColor.Yellow);

                        try
                        {
                            var result = InjectionSmoke.InjectExisting(networkPid, _dllPath, _filterPath, installHook: true);
                            Log($"[Supervisor] Successfully hooked Network Service (PID: {result.ProcessId}). Hook Status: Active ({result.HookResolutionMode}).", ConsoleColor.Green);
                        }
                        catch (Exception ex)
                        {
                            Log($"[Supervisor] Failed to inject hook into PID {networkPid}: {ex.Message}", ConsoleColor.Red);
                            lock (_sync)
                            {
                                _hookedNetworkProcesses.Remove(networkPid);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Continue polling
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MonitorDevToolsAndCosmeticsLoopAsync(string? userDataDir, int targetPort, DateTime launchStartedUtc, Func<string, string> injectionScriptFactory, CancellationToken cancellationToken)
    {
        // 1. Locate and verify DevTools port
        while (!cancellationToken.IsCancellationRequested && _activeDevToolsPort == null)
        {
            _activeDevToolsPort = await ResolveAndVerifyDevToolsPortAsync(userDataDir, targetPort, launchStartedUtc, cancellationToken);
            if (_activeDevToolsPort.HasValue)
            {
                Log($"[Supervisor] Connected to Chrome CDP on port {_activeDevToolsPort.Value}. Cosmetic & scriptlet injection active on all tabs.", ConsoleColor.Magenta);
                break;
            }

            try
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_activeDevToolsPort == null) return;

        // 2. Start continuous cosmetic injection
        await _cosmeticInjector.StartMonitoringAsync(
            () => _activeDevToolsPort,
            injectionScriptFactory,
            cancellationToken);
    }

    private static async Task<int?> ResolveAndVerifyDevToolsPortAsync(string? userDataDir, int explicitPort, DateTime launchStartedUtc, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // Trust only the DevToolsActivePort emitted by this launch's dedicated
        // profile. This prevents attaching to an unrelated Chrome instance.
        if (!string.IsNullOrWhiteSpace(userDataDir))
        {
            var portFile = Path.Combine(userDataDir, "DevToolsActivePort");
            if (File.Exists(portFile))
            {
                try
                {
                    var lines = File.ReadAllLines(portFile);
                    var freshEnough = File.GetLastWriteTimeUtc(portFile) >= launchStartedUtc.AddSeconds(-2);
                    if (freshEnough && lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0)
                    {
                        try
                        {
                            var res = await http.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
                            if (res.IsSuccessStatusCode)
                            {
                                var versionJson = await res.Content.ReadAsStringAsync(cancellationToken);
                                using var document = System.Text.Json.JsonDocument.Parse(versionJson);
                                if (document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsProperty) &&
                                    Uri.TryCreate(wsProperty.GetString(), UriKind.Absolute, out var wsUri) &&
                                    string.Equals(wsUri.AbsolutePath, lines[1].Trim(), StringComparison.Ordinal))
                                {
                                    return port;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // An explicit non-zero port remains supported for diagnostics, but only
        // when no profile was supplied. The default production path uses port 0.
        if (string.IsNullOrWhiteSpace(userDataDir) && explicitPort > 0)
        {
            try
            {
                var res = await http.GetAsync($"http://127.0.0.1:{explicitPort}/json/version", cancellationToken);
                if (res.IsSuccessStatusCode)
                {
                    return explicitPort;
                }
            }
            catch { }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Cargo.toml")) ||
                Directory.Exists(Path.Combine(current.FullName, "filters")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return AppContext.BaseDirectory;
    }

    public void Dispose()
    {
        _liveBlockMonitor?.Dispose();
        _cosmeticInjector.Dispose();
    }
}
