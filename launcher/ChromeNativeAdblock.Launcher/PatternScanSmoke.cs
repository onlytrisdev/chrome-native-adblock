using System.Diagnostics;

namespace ChromeNativeAdblock.Launcher;

public sealed record PatternScanSmokeResult(
    bool Success,
    string ChromeVersion,
    string ChromeDllPath,
    string StartRva,
    string CancelRva,
    string? Error);

public static class PatternScanSmoke
{
    public static string FindChromeDll(string chromePath)
    {
        if (File.Exists(chromePath) && chromePath.EndsWith("chrome.dll", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(chromePath);
        }

        var dir = File.Exists(chromePath) ? Path.GetDirectoryName(chromePath)! : chromePath;

        if (File.Exists(chromePath))
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(chromePath).FileVersion;
            if (!string.IsNullOrEmpty(versionInfo))
            {
                var candidate = Path.Combine(dir, versionInfo, "chrome.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var direct = Path.Combine(dir, "chrome.dll");
        if (File.Exists(direct))
        {
            return direct;
        }

        // Search subdirectories for versioned folders
        if (Directory.Exists(dir))
        {
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var cand = Path.Combine(subDir, "chrome.dll");
                if (File.Exists(cand))
                {
                    return cand;
                }
            }
        }

        throw new FileNotFoundException("chrome.dll was not found near the specified Chrome installation.", chromePath);
    }

    public static PatternScanSmokeResult Run(string chromePath, string dllPath)
    {
        try
        {
            var chromeDllPath = FindChromeDll(chromePath);
            var version = File.Exists(chromePath) && !chromePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? FileVersionInfo.GetVersionInfo(chromePath).FileVersion ?? "unknown"
                : FileVersionInfo.GetVersionInfo(chromeDllPath).FileVersion ?? "unknown";

            using var engine = new NativeEngine(dllPath);
            var (startRva, cancelRva) = engine.ScanChromeDllFile(chromeDllPath);

            var success = startRva != 0 && cancelRva != 0;

            return new PatternScanSmokeResult(
                Success: success,
                ChromeVersion: version,
                ChromeDllPath: chromeDllPath,
                StartRva: $"0x{startRva:X}",
                CancelRva: $"0x{cancelRva:X}",
                Error: null);
        }
        catch (Exception ex)
        {
            return new PatternScanSmokeResult(
                Success: false,
                ChromeVersion: "unknown",
                ChromeDllPath: chromePath,
                StartRva: "0x0",
                CancelRva: "0x0",
                Error: ex.Message);
        }
    }
}
