using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ChromeNativeAdblock.Launcher;

internal sealed record InjectionSmokeResult(
    bool Success,
    string ChromeVersion,
    uint ProcessId,
    string InjectedModule,
    string RemoteModuleBase,
    uint InitializeExitCode,
    uint HookInstallExitCode,
    bool HookInstalled,
    string HookResolutionMode,
    bool ProcessSurvivedResume);

internal sealed record RemoteInjectionResult(
    uint ProcessId,
    string InjectedModule,
    string RemoteModuleBase,
    uint InitializeExitCode,
    uint HookInstallExitCode,
    bool HookInstalled,
    string HookResolutionMode);

internal static class InjectionSmoke
{
    internal static InjectionSmokeResult Run(
        string chromePath,
        string dllPath,
        string filterPath,
        bool installHook = true,
        string extraChromeArguments = "",
        Action<string, uint>? afterHook = null)
    {
        chromePath = Path.GetFullPath(chromePath);
        dllPath = Path.GetFullPath(dllPath);
        filterPath = Path.GetFullPath(filterPath);
        if (!File.Exists(chromePath))
        {
            throw new FileNotFoundException("Chrome executable was not found.", chromePath);
        }

        var profilePath = Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock", "inject-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);
        var arguments = $"\"{chromePath}\" --headless=new --disable-gpu --no-first-run --no-default-browser-check {extraChromeArguments} " +
                        $"--user-data-dir=\"{profilePath}\" about:blank";
        var startupInfo = new NativeMethods.StartupInfo
        {
            cb = checked((uint)Marshal.SizeOf<NativeMethods.StartupInfo>())
        };
        var commandLine = new StringBuilder(arguments);
        NativeMethods.ProcessInformation processInfo = default;
        var resumed = false;

        try
        {
            if (!NativeMethods.CreateProcessW(
                    chromePath,
                    commandLine,
                    0,
                    0,
                    false,
                    NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment,
                    0,
                    Path.GetDirectoryName(chromePath)!,
                    ref startupInfo,
                    out processInfo))
            {
                throw NativeMethods.Error("CreateProcessW failed");
            }

            // Let the Windows loader finish mapping system DLLs, then inject before
            // the temporary headless session performs meaningful navigation. The
            // production launcher will use debug load events to stop at chrome.dll.
            if (NativeMethods.ResumeThread(processInfo.hThread) == uint.MaxValue)
            {
                throw NativeMethods.Error("ResumeThread failed");
            }
            resumed = true;

            var injection = Inject(processInfo.dwProcessId, processInfo.hProcess, dllPath, filterPath, installHook);

            afterHook?.Invoke(profilePath, processInfo.dwProcessId);

            using var process = Process.GetProcessById(checked((int)processInfo.dwProcessId));
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var survived = !process.HasExited;

            return new InjectionSmokeResult(
                survived,
                FileVersionInfo.GetVersionInfo(chromePath).FileVersion ?? "unknown",
                processInfo.dwProcessId,
                injection.InjectedModule,
                injection.RemoteModuleBase,
                injection.InitializeExitCode,
                injection.HookInstallExitCode,
                installHook,
                injection.HookResolutionMode,
                survived);
        }
        finally
        {
            if (processInfo.hProcess != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(checked((int)processInfo.dwProcessId));
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (ArgumentException)
                {
                }
                if (!resumed)
                {
                    _ = NativeMethods.TerminateProcess(processInfo.hProcess, 0x434e41);
                }
            }
            if (processInfo.hThread != 0)
            {
                _ = NativeMethods.CloseHandle(processInfo.hThread);
            }
            if (processInfo.hProcess != 0)
            {
                _ = NativeMethods.CloseHandle(processInfo.hProcess);
            }
            TryDeleteTemporaryProfile(profilePath);
        }
    }

    internal static RemoteInjectionResult InjectExisting(uint processId, string dllPath, string filterPath, bool installHook)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.InjectionProcessAccess, false, processId);
        if (process == 0)
        {
            throw NativeMethods.Error($"OpenProcess failed for process {processId}");
        }
        try
        {
            return Inject(processId, process, dllPath, filterPath, installHook);
        }
        finally
        {
            _ = NativeMethods.CloseHandle(process);
        }
    }

    private static RemoteInjectionResult Inject(uint processId, nint process, string dllPath, string filterPath, bool installHook)
    {
        var remoteKernel32 = FindRemoteModule(processId, "kernel32.dll");
        var localKernel32 = NativeLibrary.Load("kernel32.dll");
        nint loadLibraryRva;
        try
        {
            loadLibraryRva = NativeLibrary.GetExport(localKernel32, "LoadLibraryW") - localKernel32;
        }
        finally
        {
            NativeLibrary.Free(localKernel32);
        }

        using var remoteDllPath = RemoteAllocation.WriteUtf16(process, dllPath);
        RunRemoteThread(process, remoteKernel32.BaseAddress + loadLibraryRva, remoteDllPath.Address, "LoadLibraryW");
        var remoteDll = FindRemoteModule(processId, Path.GetFileName(dllPath));

        var localDll = NativeLibrary.Load(dllPath);
        nint initializeRva;
        nint installHookRva;
        nint getHookResolutionModeRva = 0;
        try
        {
            initializeRva = NativeLibrary.GetExport(localDll, "cna_remote_initialize") - localDll;
            installHookRva = NativeLibrary.GetExport(localDll, "cna_remote_install_network_hook") - localDll;
            if (NativeLibrary.TryGetExport(localDll, "cna_get_hook_resolution_mode", out var modeExport))
            {
                getHookResolutionModeRva = modeExport - localDll;
            }
        }
        finally
        {
            NativeLibrary.Free(localDll);
        }

        using var remoteFilterPath = RemoteAllocation.WriteUtf16(process, filterPath);
        var initializeExitCode = RunRemoteThread(
            process,
            remoteDll.BaseAddress + initializeRva,
            remoteFilterPath.Address,
            "cna_remote_initialize");
        if (initializeExitCode != 0)
        {
            throw new InvalidOperationException($"Remote engine initialization failed with exit code 0x{initializeExitCode:X8}.");
        }

        uint hookInstallExitCode = 0;
        var resolutionModeStr = installHook ? "Unknown" : "Not Installed";
        if (installHook)
        {
            _ = FindRemoteModule(processId, "chrome.dll");
            hookInstallExitCode = RunRemoteThread(
                process,
                remoteDll.BaseAddress + installHookRva,
                0,
                "cna_remote_install_network_hook");
            if (hookInstallExitCode != 0)
            {
                throw new InvalidOperationException($"Remote hook installation failed with exit code 0x{hookInstallExitCode:X8}.");
            }

            if (getHookResolutionModeRva != 0)
            {
                var modeCode = RunRemoteThread(
                    process,
                    remoteDll.BaseAddress + getHookResolutionModeRva,
                    0,
                    "cna_get_hook_resolution_mode");
                resolutionModeStr = modeCode switch
                {
                    1 => "Known RVA (Fast Path)",
                    2 => "Dynamic Pattern Scan (Auto Discovered)",
                    _ => "Unknown"
                };
            }
        }

        return new RemoteInjectionResult(
            processId,
            remoteDll.Path,
            $"0x{remoteDll.BaseAddress:X}",
            initializeExitCode,
            hookInstallExitCode,
            installHook,
            resolutionModeStr);
    }

    private static uint RunRemoteThread(nint process, nint startAddress, nint parameter, string operation)
    {
        var thread = NativeMethods.CreateRemoteThread(process, 0, 0, startAddress, parameter, 0, out _);
        if (thread == 0)
        {
            throw NativeMethods.Error($"CreateRemoteThread failed for {operation}");
        }
        try
        {
            var wait = NativeMethods.WaitForSingleObject(thread, 30_000);
            if (wait != 0)
            {
                throw new TimeoutException($"Remote operation {operation} did not complete (wait result 0x{wait:X8}).");
            }
            if (!NativeMethods.GetExitCodeThread(thread, out var exitCode))
            {
                throw NativeMethods.Error($"GetExitCodeThread failed for {operation}");
            }
            return exitCode;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(thread);
        }
    }

    private static RemoteModule FindRemoteModule(uint processId, string moduleName)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var snapshot = NativeMethods.CreateToolhelp32Snapshot(
                NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32,
                processId);
            if (snapshot == NativeMethods.InvalidHandleValue)
            {
                var error = Marshal.GetLastWin32Error();
                // The loader can transiently expose an incomplete module list while a
                // just-created process is suspended. Microsoft documents retrying
                // snapshots for ERROR_BAD_LENGTH; ERROR_PARTIAL_COPY is observed in
                // the same startup window on current Chrome.
                if (error is 24 or 299)
                {
                    Thread.Sleep(20);
                    continue;
                }
                throw new System.ComponentModel.Win32Exception(error, "CreateToolhelp32Snapshot failed");
            }
            try
            {
                var entry = new NativeMethods.ModuleEntry32
                {
                    dwSize = checked((uint)Marshal.SizeOf<NativeMethods.ModuleEntry32>())
                };
                if (NativeMethods.Module32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        if (string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            return new RemoteModule(entry.modBaseAddr, entry.szExePath);
                        }
                    }
                    while (NativeMethods.Module32NextW(snapshot, ref entry));
                }
            }
            finally
            {
                _ = NativeMethods.CloseHandle(snapshot);
            }
            Thread.Sleep(20);
        }

        throw new InvalidOperationException($"Remote module '{moduleName}' was not found in process {processId}.");
    }

    private static void TryDeleteTemporaryProfile(string profilePath)
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ChromeNativeAdblock"));
        var resolved = Path.GetFullPath(profilePath);
        if (!resolved.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(resolved))
                {
                    Directory.Delete(resolved, recursive: true);
                }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed record RemoteModule(nint BaseAddress, string Path);

    private sealed class RemoteAllocation : IDisposable
    {
        private readonly nint _process;
        internal nint Address { get; }

        private RemoteAllocation(nint process, nint address)
        {
            _process = process;
            Address = address;
        }

        internal static RemoteAllocation WriteUtf16(nint process, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value + '\0');
            var address = NativeMethods.VirtualAllocEx(
                process,
                0,
                (nuint)bytes.Length,
                NativeMethods.MemCommit | NativeMethods.MemReserve,
                NativeMethods.PageReadWrite);
            if (address == 0)
            {
                throw NativeMethods.Error("VirtualAllocEx failed");
            }
            if (!NativeMethods.WriteProcessMemory(process, address, bytes, (nuint)bytes.Length, out var written) ||
                written != (nuint)bytes.Length)
            {
                _ = NativeMethods.VirtualFreeEx(process, address, 0, NativeMethods.MemRelease);
                throw NativeMethods.Error("WriteProcessMemory failed");
            }
            return new RemoteAllocation(process, address);
        }

        public void Dispose()
        {
            if (Address != 0)
            {
                _ = NativeMethods.VirtualFreeEx(_process, Address, 0, NativeMethods.MemRelease);
            }
        }
    }
}
