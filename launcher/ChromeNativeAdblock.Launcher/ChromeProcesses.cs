using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChromeNativeAdblock.Launcher;

internal static class ChromeProcesses
{
    internal static uint WaitForNetworkService(uint browserProcessId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var pids = FindNetworkServices(browserProcessId);
            if (pids.Count > 0)
            {
                return pids[0];
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Chrome network service process was not found.");
    }

    internal static IReadOnlyList<uint> FindNetworkServices(uint? browserProcessId = null, string? userDataDir = null)
    {
        var results = new List<uint>();
        var snapshots = Snapshot();
        var normalizedUserDir = userDataDir != null ? Path.GetFullPath(userDataDir).TrimEnd('\\', '/') : null;

        foreach (var process in snapshots)
        {
            if (!string.Equals(process.Executable, "chrome.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // If browserProcessId is supplied, check descendant or matching profile
            if (browserProcessId.HasValue && browserProcessId.Value > 0)
            {
                var isChild = IsDescendant(process.ProcessId, browserProcessId.Value, process.Parents);
                if (!isChild && normalizedUserDir == null)
                {
                    continue;
                }
            }

            var commandLine = TryReadCommandLine(process.ProcessId);
            if (commandLine == null) continue;

            if (normalizedUserDir != null && !commandLine.Contains(normalizedUserDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (commandLine.Contains("--utility-sub-type=network.mojom.NetworkService", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(process.ProcessId);
            }
        }
        return results;
    }

    internal static IReadOnlyList<uint> GetRunningChromePids(string? userDataDir = null)
    {
        var results = new List<uint>();
        var snapshots = Snapshot();
        var normalizedUserDir = userDataDir != null ? Path.GetFullPath(userDataDir).TrimEnd('\\', '/') : null;

        foreach (var process in snapshots)
        {
            if (!string.Equals(process.Executable, "chrome.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (normalizedUserDir != null)
            {
                var commandLine = TryReadCommandLine(process.ProcessId);
                if (commandLine != null && commandLine.Contains(normalizedUserDir, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(process.ProcessId);
                }
            }
            else
            {
                results.Add(process.ProcessId);
            }
        }
        return results;
    }

    internal static uint? FindMainBrowserPid(string? userDataDir = null, uint? fallbackPid = null)
    {
        var snapshots = Snapshot();
        var normalizedUserDir = userDataDir != null ? Path.GetFullPath(userDataDir).TrimEnd('\\', '/') : null;

        var chromeProcesses = snapshots.Where(p => string.Equals(p.Executable, "chrome.exe", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var process in chromeProcesses)
        {
            var commandLine = TryReadCommandLine(process.ProcessId);
            if (commandLine == null) continue;

            if (normalizedUserDir != null && !commandLine.Contains(normalizedUserDir, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Main browser process command line does NOT contain --type=
            if (!commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
            {
                return process.ProcessId;
            }
        }

        if (fallbackPid.HasValue && fallbackPid.Value > 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)fallbackPid.Value);
                if (!p.HasExited) return fallbackPid.Value;
            }
            catch { }
        }

        return null;
    }

    internal static bool IsAnyChromeRunning(string? userDataDir = null, uint? browserPid = null)
    {
        if (browserPid.HasValue && browserPid.Value > 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)browserPid.Value);
                if (!p.HasExited) return true;
            }
            catch { }
        }

        var pids = GetRunningChromePids(userDataDir);
        return pids.Count > 0;
    }

    private static bool IsDescendant(uint processId, uint root, IReadOnlyDictionary<uint, uint> parents)
    {
        var current = processId;
        for (var depth = 0; depth < 32 && parents.TryGetValue(current, out var parent); depth++)
        {
            if (parent == root) return true;
            if (parent == 0 || parent == current) return false;
            current = parent;
        }
        return false;
    }

    private static IReadOnlyList<ProcessSnapshot> Snapshot()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandleValue) throw NativeMethods.Error("Process snapshot failed");
        try
        {
            var entries = new List<(uint ProcessId, uint ParentId, string Executable)>();
            var entry = new NativeMethods.ProcessEntry32
            {
                dwSize = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>())
            };
            if (NativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    entries.Add((entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
                    entry.dwSize = checked((uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>());
                }
                while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
            var parents = entries.ToDictionary(entry => entry.ProcessId, entry => entry.ParentId);
            return entries.Select(entry => new ProcessSnapshot(entry.ProcessId, entry.Executable, parents)).ToArray();
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    internal static string? TryReadCommandLine(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            _ = NativeMethods.NtQueryInformationProcess(process, 60, 0, 0, out var required);
            if (required == 0 || required > 1024 * 1024) return null;
            var buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                var status = NativeMethods.NtQueryInformationProcess(process, 60, buffer, required, out _);
                if (status < 0) return null;
                var value = Marshal.PtrToStructure<NativeMethods.UnicodeString>(buffer);
                return value.Buffer == 0 ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(process);
        }
    }

    private sealed record ProcessSnapshot(
        uint ProcessId,
        string Executable,
        IReadOnlyDictionary<uint, uint> Parents);
}
