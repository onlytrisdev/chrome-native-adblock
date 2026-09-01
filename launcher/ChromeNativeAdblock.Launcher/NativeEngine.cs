using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeNativeAdblock.Launcher;

public enum HookResolutionMode : uint
{
    NotInstalled = 0,
    KnownRva = 1,
    DynamicPatternScan = 2,
}

public sealed record CosmeticResources(
    [property: JsonPropertyName("hide_selectors")] string[] HideSelectors,
    [property: JsonPropertyName("injected_script")] string InjectedScript,
    [property: JsonPropertyName("exceptions")] string[] Exceptions);
public sealed unsafe class NativeEngine : IDisposable
{
    private readonly nint _module;

    private readonly delegate* unmanaged[Cdecl]<byte*> _versionFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, int> _loadFilterTextFunc;
    private readonly delegate* unmanaged[Cdecl]<char*, int> _loadFilterFileFunc;
    private readonly delegate* unmanaged[Stdcall]<char*, uint> _remoteInitializeFunc;
    private readonly delegate* unmanaged[Stdcall]<nint, uint> _remoteInstallHookFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, byte*, nuint, byte*, nuint, int> _checkFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint> _urlCosmeticResourcesFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, nuint> _serializeFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, int> _deserializeFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, nuint> _lastErrorFunc;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, int> _logNetworkBlockFunc;
    private readonly delegate* unmanaged[Stdcall]<nint, uint> _getHookResolutionModeFunc;
    private readonly delegate* unmanaged[Cdecl]<nuint*, nuint*, uint> _getHookInfoFunc;
    private readonly delegate* unmanaged[Cdecl]<char*, nuint*, nuint*, int> _scanChromeDllFileFunc;

    public NativeEngine(string dllPath)
    {
        _module = NativeLibrary.Load(dllPath);
        _versionFunc = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(_module, "cna_engine_version");
        _loadFilterTextFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)NativeLibrary.GetExport(_module, "cna_engine_load_filter_text_utf8");
        _loadFilterFileFunc = (delegate* unmanaged[Cdecl]<char*, int>)NativeLibrary.GetExport(_module, "cna_engine_load_filter_file_utf16");
        _remoteInitializeFunc = (delegate* unmanaged[Stdcall]<char*, uint>)NativeLibrary.GetExport(_module, "cna_remote_initialize");
        _remoteInstallHookFunc = (delegate* unmanaged[Stdcall]<nint, uint>)NativeLibrary.GetExport(_module, "cna_remote_install_network_hook");
        _checkFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, byte*, nuint, byte*, nuint, int>)NativeLibrary.GetExport(_module, "cna_engine_check_utf8");
        _urlCosmeticResourcesFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint>)NativeLibrary.GetExport(_module, "cna_engine_url_cosmetic_resources_utf8");
        _serializeFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, nuint>)NativeLibrary.GetExport(_module, "cna_engine_serialize");
        _deserializeFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)NativeLibrary.GetExport(_module, "cna_engine_deserialize");
        _lastErrorFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, nuint>)NativeLibrary.GetExport(_module, "cna_engine_last_error_utf8");
        if (NativeLibrary.TryGetExport(_module, "cna_log_network_block_utf8", out var logPtr))
        {
            _logNetworkBlockFunc = (delegate* unmanaged[Cdecl]<byte*, nuint, int>)logPtr;
        }
        if (NativeLibrary.TryGetExport(_module, "cna_get_hook_resolution_mode", out var modePtr))
        {
            _getHookResolutionModeFunc = (delegate* unmanaged[Stdcall]<nint, uint>)modePtr;
        }
        if (NativeLibrary.TryGetExport(_module, "cna_get_hook_info", out var infoPtr))
        {
            _getHookInfoFunc = (delegate* unmanaged[Cdecl]<nuint*, nuint*, uint>)infoPtr;
        }
        if (NativeLibrary.TryGetExport(_module, "cna_scan_chrome_dll_file_utf16", out var scanPtr))
        {
            _scanChromeDllFileFunc = (delegate* unmanaged[Cdecl]<char*, nuint*, nuint*, int>)scanPtr;
        }
    }

    public string GetVersion()
    {
        var ptr = _versionFunc();
        return Marshal.PtrToStringUTF8((nint)ptr) ?? string.Empty;
    }

    public void LoadFilterText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* ptr = bytes)
        {
            var res = _loadFilterTextFunc(ptr, (nuint)bytes.Length);
            if (res != 0)
            {
                throw new InvalidOperationException($"LoadFilterText failed: {GetLastError()}");
            }
        }
    }

    public void LoadFilterFile(string path)
    {
        var nullTerminated = path + '\0';
        fixed (char* ptr = nullTerminated)
        {
            var res = _loadFilterFileFunc(ptr);
            if (res != 0)
            {
                throw new InvalidOperationException($"LoadFilterFile failed: {GetLastError()}");
            }
        }
    }

    public bool CheckRequest(string url, string sourceUrl, string requestType, string method)
    {
        var urlBytes = Encoding.UTF8.GetBytes(url);
        var sourceBytes = Encoding.UTF8.GetBytes(sourceUrl);
        var typeBytes = Encoding.UTF8.GetBytes(requestType);
        var methodBytes = Encoding.UTF8.GetBytes(method);
        fixed (byte* urlPtr = urlBytes)
        fixed (byte* srcPtr = sourceBytes)
        fixed (byte* typePtr = typeBytes)
        fixed (byte* methodPtr = methodBytes)
        {
            var res = _checkFunc(
                urlPtr, (nuint)urlBytes.Length,
                srcPtr, (nuint)sourceBytes.Length,
                typePtr, (nuint)typeBytes.Length,
                methodPtr, (nuint)methodBytes.Length);
            if (res < 0)
            {
                throw new InvalidOperationException($"CheckRequest failed: {GetLastError()}");
            }
            return res == 1;
        }
    }

    public CosmeticResources GetUrlCosmeticResources(string url)
    {
        var urlBytes = Encoding.UTF8.GetBytes(url);
        fixed (byte* urlPtr = urlBytes)
        {
            var required = _urlCosmeticResourcesFunc(urlPtr, (nuint)urlBytes.Length, null, 0);
            if (required == 0)
            {
                return new CosmeticResources([], "", []);
            }

            var buffer = new byte[checked((int)required + 1)];
            fixed (byte* bufPtr = buffer)
            {
                var written = _urlCosmeticResourcesFunc(urlPtr, (nuint)urlBytes.Length, bufPtr, (nuint)buffer.Length);
                var jsonStr = Encoding.UTF8.GetString(buffer, 0, checked((int)written));
                return JsonSerializer.Deserialize<CosmeticResources>(jsonStr) ?? new CosmeticResources([], "", []);
            }
        }
    }

    public byte[] Serialize()
    {
        var required = _serializeFunc(null, 0);
        if (required == 0)
        {
            throw new InvalidOperationException($"Serialize failed: {GetLastError()}");
        }

        var buffer = new byte[checked((int)required)];
        fixed (byte* bufPtr = buffer)
        {
            var written = _serializeFunc(bufPtr, (nuint)buffer.Length);
            if (written == 0)
            {
                throw new InvalidOperationException($"Serialize copy failed: {GetLastError()}");
            }
            return buffer;
        }
    }

    public void Deserialize(byte[] data)
    {
        if (data.Length == 0)
        {
            throw new ArgumentException("Data cannot be empty.", nameof(data));
        }

        fixed (byte* dataPtr = data)
        {
            var res = _deserializeFunc(dataPtr, (nuint)data.Length);
            if (res != 0)
            {
                throw new InvalidOperationException($"Deserialize failed: {GetLastError()}");
            }
        }
    }

    public string GetLastError()
    {
        var required = _lastErrorFunc(null, 0);
        if (required == 0) return string.Empty;

        var buffer = new byte[checked((int)required + 1)];
        fixed (byte* bufPtr = buffer)
        {
            _ = _lastErrorFunc(bufPtr, (nuint)buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, checked((int)required));
        }
    }

    public void LogNetworkBlock(string url)
    {
        if (_logNetworkBlockFunc == null) return;
        var urlBytes = Encoding.UTF8.GetBytes(url);
        fixed (byte* urlPtr = urlBytes)
        {
            _logNetworkBlockFunc(urlPtr, (nuint)urlBytes.Length);
        }
    }

    public HookResolutionMode GetHookResolutionMode()
    {
        if (_getHookResolutionModeFunc == null) return HookResolutionMode.NotInstalled;
        return (HookResolutionMode)_getHookResolutionModeFunc(0);
    }

    public (HookResolutionMode Mode, nuint StartRva, nuint CancelRva) GetHookInfo()
    {
        if (_getHookInfoFunc == null) return (HookResolutionMode.NotInstalled, 0, 0);
        nuint start = 0;
        nuint cancel = 0;
        var mode = (HookResolutionMode)_getHookInfoFunc(&start, &cancel);
        return (mode, start, cancel);
    }

    public (nuint StartRva, nuint CancelRva) ScanChromeDllFile(string chromeDllPath)
    {
        if (_scanChromeDllFileFunc == null)
        {
            throw new NotSupportedException("cna_scan_chrome_dll_file_utf16 is not supported by this native library.");
        }
        fixed (char* pPath = chromeDllPath)
        {
            nuint start = 0;
            nuint cancel = 0;
            var res = _scanChromeDllFileFunc(pPath, &start, &cancel);
            if (res != 0)
            {
                var err = GetLastError();
                throw new InvalidOperationException($"Dynamic pattern scan of '{chromeDllPath}' failed: {err}");
            }
            return (start, cancel);
        }
    }

    public void Dispose()
    {
        NativeLibrary.Free(_module);
    }
}
