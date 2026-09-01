using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaFlux.Services;

public enum NativeAiStatus { Ok = 0, NotImplemented = 1, InvalidArgument = 2, SdkMismatch = 3, InvalidHandle = 4, Cancelled = 5, InternalError = 6 }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAiVersion { public uint Major; public uint Minor; public uint Patch; public NativeAiVersion(uint major, uint minor, uint patch = 0) { Major = major; Minor = minor; Patch = patch; } public override readonly string ToString() => $"{Major}.{Minor}.{Patch}"; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAiCapabilities { public uint StructSize; public ulong Flags; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAiImage { public uint StructSize; public int Width; public int Height; public int Stride; public int PixelFormat; public IntPtr Data; public nuint DataSize; }

public sealed class NativeAiProviderException : InvalidOperationException
{
    public NativeAiProviderException(NativeAiStatus status, string message) : base(message) { Status = status; }
    public NativeAiStatus Status { get; }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NativeAiLogger(int level, [MarshalAs(UnmanagedType.LPUTF8Str)] string message, IntPtr context);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NativeAiProgress(double fraction, [MarshalAs(UnmanagedType.LPUTF8Str)] string stage, IntPtr context);

internal interface INativeAiProviderApi : IDisposable
{
    NativeAiStatus GetVersion(out NativeAiVersion version);
    NativeAiStatus Initialize(in NativeAiVersion requested, out NativeAiVersion negotiated);
    NativeAiStatus Shutdown();
    NativeAiStatus GetCapabilities(ref NativeAiCapabilities capabilities);
    NativeAiStatus CreateProvider(out IntPtr provider);
    NativeAiStatus DestroyProvider(IntPtr provider);
    NativeAiStatus CancelOperation(IntPtr provider);
    NativeAiStatus ReleaseResources(IntPtr provider);
    NativeAiStatus GetLastError(IntPtr provider, byte[]? buffer, ref nuint size);
    NativeAiStatus RegisterLogger(IntPtr provider, NativeAiLogger callback, IntPtr context);
    NativeAiStatus RegisterProgress(IntPtr provider, NativeAiProgress callback, IntPtr context);
    NativeAiStatus ProcessImage(IntPtr provider, in NativeAiImage input, ref NativeAiImage output);
}

internal sealed class DynamicNativeAiProviderApi : INativeAiProviderApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus VersionDelegate(out NativeAiVersion version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus InitializeDelegate(in NativeAiVersion requested, out NativeAiVersion negotiated);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus NoArgDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus CapabilitiesDelegate(ref NativeAiCapabilities capabilities);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus CreateDelegate(out IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus ProviderDelegate(IntPtr provider);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus ErrorDelegate(IntPtr provider, byte[]? buffer, ref nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus LoggerDelegate(IntPtr provider, NativeAiLogger callback, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus ProgressDelegate(IntPtr provider, NativeAiProgress callback, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate NativeAiStatus ProcessDelegate(IntPtr provider, in NativeAiImage input, ref NativeAiImage output);

    private IntPtr _library;
    private readonly VersionDelegate _getVersion; private readonly InitializeDelegate _initialize; private readonly NoArgDelegate _shutdown; private readonly CapabilitiesDelegate _getCapabilities; private readonly CreateDelegate _create; private readonly ProviderDelegate _destroy, _cancel, _release; private readonly ErrorDelegate _error; private readonly LoggerDelegate _logger; private readonly ProgressDelegate _progress; private readonly ProcessDelegate _process;
    public DynamicNativeAiProviderApi(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Native AI provider DLL path is required.", nameof(path));
        _library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            _getVersion = Export<VersionDelegate>("MfAi_GetVersion"); _initialize = Export<InitializeDelegate>("MfAi_Initialize"); _shutdown = Export<NoArgDelegate>("MfAi_Shutdown"); _getCapabilities = Export<CapabilitiesDelegate>("MfAi_GetCapabilities"); _create = Export<CreateDelegate>("MfAi_CreateProvider"); _destroy = Export<ProviderDelegate>("MfAi_DestroyProvider"); _cancel = Export<ProviderDelegate>("MfAi_CancelOperation"); _release = Export<ProviderDelegate>("MfAi_ReleaseResources"); _error = Export<ErrorDelegate>("MfAi_GetLastError"); _logger = Export<LoggerDelegate>("MfAi_RegisterLogger"); _progress = Export<ProgressDelegate>("MfAi_RegisterProgressCallback"); _process = Export<ProcessDelegate>("MfAi_ProcessImage");
        }
        catch { Dispose(); throw; }
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    public NativeAiStatus GetVersion(out NativeAiVersion version) => _getVersion(out version); public NativeAiStatus Initialize(in NativeAiVersion requested, out NativeAiVersion negotiated) => _initialize(in requested, out negotiated); public NativeAiStatus Shutdown() => _shutdown(); public NativeAiStatus GetCapabilities(ref NativeAiCapabilities capabilities) => _getCapabilities(ref capabilities); public NativeAiStatus CreateProvider(out IntPtr provider) => _create(out provider); public NativeAiStatus DestroyProvider(IntPtr provider) => _destroy(provider); public NativeAiStatus CancelOperation(IntPtr provider) => _cancel(provider); public NativeAiStatus ReleaseResources(IntPtr provider) => _release(provider); public NativeAiStatus GetLastError(IntPtr provider, byte[]? buffer, ref nuint size) => _error(provider, buffer, ref size); public NativeAiStatus RegisterLogger(IntPtr provider, NativeAiLogger callback, IntPtr context) => _logger(provider, callback, context); public NativeAiStatus RegisterProgress(IntPtr provider, NativeAiProgress callback, IntPtr context) => _progress(provider, callback, context); public NativeAiStatus ProcessImage(IntPtr provider, in NativeAiImage input, ref NativeAiImage output) => _process(provider, in input, ref output);
    public void Dispose() { IntPtr handle = Interlocked.Exchange(ref _library, IntPtr.Zero); if (handle != IntPtr.Zero) NativeLibrary.Free(handle); }
}

internal sealed class SafeNativeAiProviderHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly INativeAiProviderApi _api;
    public SafeNativeAiProviderHandle(IntPtr handle, INativeAiProviderApi api) : base(true) { _api = api; SetHandle(handle); }
    protected override bool ReleaseHandle() => _api.DestroyProvider(handle) == NativeAiStatus.Ok;
}

/// <summary>Managed lifecycle and error boundary for the versioned native provider ABI.</summary>
public sealed class NativeAiProviderBridge : IDisposable
{
    private readonly INativeAiProviderApi _api; private readonly bool _ownsApi; private readonly SafeNativeAiProviderHandle _provider; private readonly NativeAiLogger _logger; private readonly NativeAiProgress _progress; private readonly List<string> _diagnostics = new(); private int _disposed;
    public NativeAiProviderBridge(string dllPath, AiProviderSdkVersion requestedVersion, Action<string>? log = null, Action<double, string>? progress = null) : this(new DynamicNativeAiProviderApi(dllPath), requestedVersion, log, progress, ownsApi: true) { }
    internal NativeAiProviderBridge(INativeAiProviderApi api, AiProviderSdkVersion requestedVersion, Action<string>? log = null, Action<double, string>? progress = null, bool ownsApi = false)
    {
        _api = api; _ownsApi = ownsApi; _logger = (_, message, _) => { _diagnostics.Add(message); log?.Invoke(message); }; _progress = (fraction, stage, _) => progress?.Invoke(fraction, stage);
        try
        {
            Throw(_api.GetVersion(out NativeAiVersion providerVersion), IntPtr.Zero, "query native provider version"); ProviderVersion = providerVersion.ToString();
            NativeAiVersion requested = new((uint)requestedVersion.Major, (uint)requestedVersion.Minor); NativeAiStatus negotiation = _api.Initialize(in requested, out NativeAiVersion negotiated);
            if (negotiation == NativeAiStatus.SdkMismatch) throw new NotSupportedException($"Native AI provider SDK {ProviderVersion} is incompatible with requested SDK {requestedVersion}.");
            Throw(negotiation, IntPtr.Zero, "initialize native provider"); NegotiatedSdkVersion = new((int)negotiated.Major, (int)negotiated.Minor);
            var capabilities = new NativeAiCapabilities { StructSize = (uint)Marshal.SizeOf<NativeAiCapabilities>() }; Throw(_api.GetCapabilities(ref capabilities), IntPtr.Zero, "query native provider capabilities"); CapabilityFlags = capabilities.Flags;
            Throw(_api.CreateProvider(out IntPtr provider), IntPtr.Zero, "create native provider"); _provider = new(provider, _api);
            Throw(_api.RegisterLogger(provider, _logger, IntPtr.Zero), provider, "register native logger"); Throw(_api.RegisterProgress(provider, _progress, IntPtr.Zero), provider, "register native progress callback");
            _diagnostics.Add($"Native bridge loaded; SDK={NegotiatedSdkVersion}; provider={ProviderVersion}; capabilities=0x{CapabilityFlags:X}.");
        }
        catch { _api.Shutdown(); if (_ownsApi) _api.Dispose(); throw; }
    }
    public string ProviderVersion { get; } = "Unavailable"; public AiProviderSdkVersion NegotiatedSdkVersion { get; } = new(0, 0); public ulong CapabilityFlags { get; }
    public IReadOnlyList<string> Diagnostics => _diagnostics.ToArray(); public bool IsClosed => _provider.IsClosed;
    public void Cancel() { Throw(_api.CancelOperation(_provider.DangerousGetHandle()), _provider.DangerousGetHandle(), "cancel native operation"); }
    public void ReleaseResources() { Throw(_api.ReleaseResources(_provider.DangerousGetHandle()), _provider.DangerousGetHandle(), "release native provider resources"); }
    public AiProviderError ProcessImageStub()
    {
        var input = new NativeAiImage { StructSize = (uint)Marshal.SizeOf<NativeAiImage>() }; var output = new NativeAiImage { StructSize = (uint)Marshal.SizeOf<NativeAiImage>() }; NativeAiStatus status = _api.ProcessImage(_provider.DangerousGetHandle(), in input, ref output);
        return status == NativeAiStatus.NotImplemented ? new(AiProviderErrorCode.ProcessingFailed, LastError(_provider.DangerousGetHandle(), "ProcessImage is not implemented.")) : status == NativeAiStatus.Cancelled ? new(AiProviderErrorCode.Cancelled, LastError(_provider.DangerousGetHandle(), "Operation cancelled.")) : status == NativeAiStatus.Ok ? new(AiProviderErrorCode.ProcessingFailed, "Stub unexpectedly accepted ProcessImage.") : new(AiProviderErrorCode.ProcessingFailed, LastError(_provider.DangerousGetHandle(), $"Native ProcessImage failed ({status})."));
    }
    private void Throw(NativeAiStatus status, IntPtr provider, string action) { if (status != NativeAiStatus.Ok) throw new NativeAiProviderException(status, LastError(provider, $"Failed to {action}: {status}.")); }
    private string LastError(IntPtr provider, string fallback)
    {
        if (provider == IntPtr.Zero) return fallback; nuint size = 0; if (_api.GetLastError(provider, null, ref size) != NativeAiStatus.Ok || size <= 1) return fallback; byte[] buffer = new byte[(int)size]; return _api.GetLastError(provider, buffer, ref size) == NativeAiStatus.Ok ? Encoding.UTF8.GetString(buffer, 0, Math.Max(0, (int)size - 1)) : fallback;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; try { if (!_provider.IsInvalid) _api.ReleaseResources(_provider.DangerousGetHandle()); } catch { } _provider.Dispose(); try { _api.Shutdown(); } finally { if (_ownsApi) _api.Dispose(); }
    }
}
