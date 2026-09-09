using System.Text.Json;

namespace ChromeNativeAdblock.Launcher;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(a => string.Equals(a, "--gui", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "gui", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-g", StringComparison.OrdinalIgnoreCase)))
            {
                return LaunchGui();
            }

            var isDebug = args.Any(a => string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-d", StringComparison.OrdinalIgnoreCase));
            var noMv2 = args.Any(a => string.Equals(a, "--no-mv2", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--adblock-only", StringComparison.OrdinalIgnoreCase));
            var noAdblock = args.Any(a => string.Equals(a, "--no-adblock", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--mv2-only", StringComparison.OrdinalIgnoreCase));
            var noUpdate = args.Any(a => string.Equals(a, "--no-update", StringComparison.OrdinalIgnoreCase));
            var openExtensions = args.Any(a => string.Equals(a, "--extensions", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--open-extensions", StringComparison.OrdinalIgnoreCase));

            var filteredArgs = args.Where(a =>
                !string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "-d", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--no-mv2", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--adblock-only", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--no-adblock", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--mv2-only", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--no-update", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--extensions", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(a, "--open-extensions", StringComparison.OrdinalIgnoreCase)).ToArray();

            var command = filteredArgs.Length > 0 ? filteredArgs[0].ToLowerInvariant() : "gui";

            switch (command)
            {
                case "gui":
                case "--gui":
                case "-g":
                    return LaunchGui();
                case "run":
                case "--run":
                case "start":
                {
                    var root = FindRepositoryRoot();
                    var dllPath = File.Exists(Path.Combine(root, "target", "release", "chrome_native_adblock.dll"))
                        ? Path.Combine(root, "target", "release", "chrome_native_adblock.dll")
                        : Path.Combine(AppContext.BaseDirectory, "chrome_native_adblock.dll");

                    var repositoryFiltersDir = Path.Combine(root, "filters");
                    var filtersDir = Directory.Exists(repositoryFiltersDir)
                        ? repositoryFiltersDir
                        : Path.Combine(AppContext.BaseDirectory, "filters");
                    // FilterManager creates this file after refreshing subscriptions.
                    // Never pin a normal run to the tiny smoke-test ruleset merely
                    // because combined_rules.txt does not exist yet.
                    var filterPath = Path.Combine(filtersDir, "combined_rules.txt");

                    var extraArgs = filteredArgs.Length > 1 ? filteredArgs.Skip(1).ToArray() : null;

                    var options = new ChromeSupervisorOptions(
                        DllPath: dllPath,
                        FilterPath: filterPath,
                        AdditionalArguments: extraArgs,
                        EnableNativeAdblock: !noAdblock,
                        EnableMv2Enabler: !noMv2,
                        AutoUpdateFilters: !noUpdate,
                        OpenExtensionsPage: openExtensions,
                        Debug: isDebug);

                    using var supervisor = new ChromeSupervisor(options);
                    return await supervisor.RunAsync();
                }
                case "mv2":
                case "mv2-run":
                {
                    var extraArgs = filteredArgs.Length > 1 ? filteredArgs.Skip(1).ToArray() : null;
                    var options = new ChromeSupervisorOptions(
                        AdditionalArguments: extraArgs,
                        EnableNativeAdblock: false,
                        EnableMv2Enabler: true,
                        AutoUpdateFilters: false,
                        OpenExtensionsPage: openExtensions,
                        Debug: isDebug);

                    using var supervisor = new ChromeSupervisor(options);
                    return await supervisor.RunAsync();
                }
                case "analyze-mv2":
                case "analyze":
                {
                    var chromePath = filteredArgs.Length >= 2 ? Path.GetFullPath(filteredArgs[1]) : ChromeInstallation.FindStableChrome();
                    var installation = ChromeInstallationFinder.Find(chromePath);
                    var report = AnalysisService.Analyze(installation);
                    Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
                    return report.Success ? 0 : 1;
                }
                case "repair-profiles":
                case "repair":
                {
                    var chromePath = filteredArgs.Length >= 2 ? Path.GetFullPath(filteredArgs[1]) : ChromeInstallation.FindStableChrome();
                    var installation = ChromeInstallationFinder.Find(chromePath);
                    var extraArgs = filteredArgs.Length > 2 ? filteredArgs.Skip(2).ToArray() : Array.Empty<string>();
                    var result = ChromeProfileRepair.RepairBeforeLaunch(installation, extraArgs);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return 0;
                }
                case "patch-running":
                {
                    var chromePath = filteredArgs.Length >= 2 ? Path.GetFullPath(filteredArgs[1]) : ChromeInstallation.FindStableChrome();
                    var installation = ChromeInstallationFinder.Find(chromePath);
                    var peImage = PeImage.Load(installation.DllPath);
                    var locator = Mv2GateLocator.Locate(peImage);
                    if (!locator.Success || locator.Target == null)
                    {
                        Console.Error.WriteLine($"MV2 Gate Locator failed: {string.Join("; ", locator.Diagnostics)}");
                        return 1;
                    }
                    var patchedPids = ChromeDebugLauncher.PatchExistingChromeProcesses(locator.Target);
                    Console.WriteLine($"Patched {patchedPids.Count} running Chrome process(es) in RAM: [{string.Join(", ", patchedPids)}]");
                    return 0;
                }
                case "engine-smoke":
                {
                    var paths = ResolvePaths(args);
                    var result = EngineSmoke.Run(paths.DllPath, paths.FilterPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "pattern-scan-smoke":
                case "scan-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = PatternScanSmoke.Run(chromePath, paths.DllPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "cosmetic-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = CosmeticSmoke.Run(chromePath, paths.DllPath, paths.FilterPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "inject-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = InjectionSmoke.Run(chromePath, paths.DllPath, paths.FilterPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "network-smoke":
                {
                    var root = FindRepositoryRoot();
                    var dllPath = args.Length >= 2
                        ? Path.GetFullPath(args[1])
                        : File.Exists(Path.Combine(root, "target", "release", "chrome_native_adblock.dll"))
                            ? Path.Combine(root, "target", "release", "chrome_native_adblock.dll")
                            : Path.Combine(AppContext.BaseDirectory, "chrome_native_adblock.dll");
                    var filterPath = args.Length >= 3
                        ? Path.GetFullPath(args[2])
                        : File.Exists(Path.Combine(root, "filters", "smoke.txt"))
                            ? Path.Combine(root, "filters", "smoke.txt")
                            : File.Exists(Path.Combine(AppContext.BaseDirectory, "filters", "smoke.txt"))
                                ? Path.Combine(AppContext.BaseDirectory, "filters", "smoke.txt")
                                : ResolvePaths(args).FilterPath;
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = NetworkSmoke.Run(chromePath, dllPath, filterPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "batch-youtube-smoke":
                case "youtube-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = await BatchYouTubeSmoke.RunAsync(chromePath, paths.DllPath, paths.FilterPath);
                    return result.Success ? 0 : 1;
                }
                case "vnexpress-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = VnExpressSmoke.Run(chromePath, paths.DllPath, paths.FilterPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.Success ? 0 : 1;
                }
                case "web-smoke":
                case "realweb-smoke":
                case "real-smoke":
                {
                    var paths = ResolvePaths(args);
                    var chromePath = args.Length >= 4 ? Path.GetFullPath(args[3]) : ChromeInstallation.FindStableChrome();
                    var result = await RealWebSmoke.RunAsync(chromePath, paths.DllPath, paths.FilterPath);
                    return result.Success ? 0 : 1;
                }
                case "update-filters":
                {
                    var root = FindRepositoryRoot();
                    var filterDir = Directory.Exists(Path.Combine(root, "filters"))
                        ? Path.Combine(root, "filters")
                        : Path.Combine(AppContext.BaseDirectory, "filters");
                    var manager = new FilterManager(filterDir);
                    await manager.EnsureFiltersReadyAsync(true);
                    Console.WriteLine($"Filters updated. Combined rules at: {manager.GetActiveCombinedFilterPath()}");
                    return 0;
                }
                default:
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static (string DllPath, string FilterPath) ResolvePaths(string[] args)
    {
        var root = FindRepositoryRoot();
        var dllPath = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : File.Exists(Path.Combine(root, "target", "release", "chrome_native_adblock.dll"))
                ? Path.Combine(root, "target", "release", "chrome_native_adblock.dll")
                : Path.Combine(AppContext.BaseDirectory, "chrome_native_adblock.dll");

        var filterPath = args.Length >= 3
            ? Path.GetFullPath(args[2])
            : File.Exists(Path.Combine(root, "filters", "combined_rules.txt"))
                ? Path.Combine(root, "filters", "combined_rules.txt")
                : File.Exists(Path.Combine(root, "filters", "smoke.txt"))
                    ? Path.Combine(root, "filters", "smoke.txt")
                    : Path.Combine(AppContext.BaseDirectory, "filters", "smoke.txt");

        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("Native engine DLL was not found.", dllPath);
        }
        if (!File.Exists(filterPath))
        {
            throw new FileNotFoundException("Filter list was not found.", filterPath);
        }
        return (dllPath, filterPath);
    }

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

    private static void PrintUsage()
    {
        Console.WriteLine("Chrome Native Adblock & Launcher");
        Console.WriteLine("Usage:");
        Console.WriteLine("  ChromeNativeAdblock.Launcher [gui / --gui]     Launch Modern WinUI 3 GUI Dashboard (Default)");
        Console.WriteLine("  ChromeNativeAdblock.Launcher run [options]     Run directly in CLI mode without GUI");
        Console.WriteLine("    Options:");
        Console.WriteLine("      --adblock-only / --no-mv2       Run native adblock only (no MV2 RAM patch)");
        Console.WriteLine("      --mv2-only / --no-adblock       Run MV2 RAM enabler only (no native network hook)");
        Console.WriteLine("      --no-update                     Skip auto-updating adblock filters");
        Console.WriteLine("      --extensions                    Open chrome://extensions page on start");
        Console.WriteLine("      --debug, -d                     Enable verbose debug logging");
        Console.WriteLine();
        Console.WriteLine("  ChromeNativeAdblock.Launcher mv2 [extra chrome args]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher analyze-mv2 [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher repair-profiles [chrome.exe] [extra args]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher patch-running [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher update-filters");
        Console.WriteLine("  ChromeNativeAdblock.Launcher engine-smoke [dll] [filter]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher cosmetic-smoke [dll] [filter] [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher pattern-scan-smoke [dll] [filter] [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher inject-smoke [dll] [filter] [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher network-smoke [dll] [filter] [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher batch-youtube-smoke [dll] [filter] [chrome.exe]");
        Console.WriteLine("  ChromeNativeAdblock.Launcher vnexpress-smoke [dll] [filter] [chrome.exe]");
    }
    private static int LaunchGui()
    {
        var root = FindRepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ChromeNativeAdblock.Gui.exe"),
            Path.Combine(AppContext.BaseDirectory, "gui", "ChromeNativeAdblock.Gui.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "gui", "ChromeNativeAdblock.Gui.exe"),
            Path.Combine(root, "dist", "gui", "ChromeNativeAdblock.Gui.exe"),
            Path.Combine(root, "gui", "ChromeNativeAdblock.Gui", "bin", "Release", "net10.0-windows10.0.26100.0", "win-x64", "ChromeNativeAdblock.Gui.exe"),
            Path.Combine(root, "gui", "ChromeNativeAdblock.Gui", "bin", "x64", "Release", "net10.0-windows10.0.26100.0", "win-x64", "ChromeNativeAdblock.Gui.exe")
        };

        var guiExe = candidates.FirstOrDefault(File.Exists);
        if (guiExe == null)
        {
            Console.Error.WriteLine("Error: ChromeNativeAdblock.Gui.exe not found.");
            return 1;
        }

        Console.WriteLine($"[Launcher] Launching GUI: {guiExe}");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = guiExe,
            WorkingDirectory = Path.GetDirectoryName(guiExe)!,
            UseShellExecute = true
        };

        System.Diagnostics.Process.Start(startInfo);
        return 0;
    }


    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
}
