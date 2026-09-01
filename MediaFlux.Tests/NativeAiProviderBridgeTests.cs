using System.Runtime.InteropServices;
using System.Text;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class NativeAiProviderBridgeTests
{
    [Fact]
    public void ManagedBridgeNegotiatesLifecycleCallbacksAndDeterministicCleanup()
    {
        var api = new FakeNativeApi(); var logs = new List<string>(); var progress = new List<double>();
        var bridge = new NativeAiProviderBridge(api, AiProviderSdk.CurrentVersion, logs.Add, (fraction, _) => progress.Add(fraction));

        Assert.Equal("1.0.0", bridge.ProviderVersion);
        Assert.Equal(AiProviderSdk.CurrentVersion, bridge.NegotiatedSdkVersion);
        Assert.Equal(15ul, bridge.CapabilityFlags);
        Assert.NotEmpty(logs);
        Assert.NotEmpty(progress);
        bridge.Cancel(); bridge.ReleaseResources(); bridge.Dispose(); bridge.Dispose();

        Assert.True(bridge.IsClosed);
        Assert.Equal(1, api.DestroyCalls);
        Assert.Equal(1, api.ShutdownCalls);
        Assert.True(api.ReleaseCalls >= 1);
    }

    [Fact]
    public void SdkMismatchFailsBeforeProviderAllocation()
    {
        var api = new FakeNativeApi { ProviderVersion = new(2, 0, 0) };
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => new NativeAiProviderBridge(api, AiProviderSdk.CurrentVersion));
        Assert.Contains("incompatible", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, api.CreateCalls);
        Assert.Equal(1, api.ShutdownCalls);
    }

    [Fact]
    public void StubProcessImageTranslatesStructuredNotImplementedError()
    {
        var api = new FakeNativeApi(); using var bridge = new NativeAiProviderBridge(api, AiProviderSdk.CurrentVersion);
        AiProviderError error = bridge.ProcessImageStub();
        Assert.Equal(AiProviderErrorCode.ProcessingFailed, error.Code);
        Assert.Contains("not implemented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeFailureIncludesStatusAndLastError()
    {
        var api = new FakeNativeApi { CancelStatus = NativeAiStatus.InternalError, LastErrorText = "native cancellation failed" }; using var bridge = new NativeAiProviderBridge(api, AiProviderSdk.CurrentVersion);
        NativeAiProviderException error = Assert.Throws<NativeAiProviderException>(() => bridge.Cancel());
        Assert.Equal(NativeAiStatus.InternalError, error.Status);
        Assert.Contains("native cancellation failed", error.Message);
    }

    [Fact]
    public void MissingDllFailsGracefully()
    {
        string missing = Path.Combine(Path.GetTempPath(), "MediaFluxNativeMissing", Guid.NewGuid().ToString("N"), "MediaFlux.NativeAiProvider.dll");
        Assert.Throws<DllNotFoundException>(() => new NativeAiProviderBridge(missing, AiProviderSdk.CurrentVersion));
    }

    [Fact]
    public void NativeProjectDefinesEveryRequiredExportAndVersionResource()
    {
        string root = FindRepositoryRoot();
        string header = File.ReadAllText(Path.Combine(root, "MediaFlux.NativeAiProvider", "MediaFlux.NativeAiProvider.h"));
        foreach (string export in new[] { "MfAi_Initialize", "MfAi_Shutdown", "MfAi_GetVersion", "MfAi_GetCapabilities", "MfAi_CreateProvider", "MfAi_DestroyProvider", "MfAi_CancelOperation", "MfAi_ReleaseResources", "MfAi_GetLastError", "MfAi_RegisterLogger", "MfAi_RegisterProgressCallback", "MfAi_ProcessImage" }) Assert.Contains(export, header);
        Assert.Contains("FILEVERSION 1,0,0,0", File.ReadAllText(Path.Combine(root, "MediaFlux.NativeAiProvider", "MediaFlux.NativeAiProvider.rc")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaFlux.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeNativeApi : INativeAiProviderApi
    {
        private readonly IntPtr _provider = new(1234); private NativeAiLogger? _logger; private NativeAiProgress? _progress;
        public NativeAiVersion ProviderVersion = new(1, 0, 0); public NativeAiStatus CancelStatus = NativeAiStatus.Ok; public string LastErrorText = "ProcessImage is not implemented by the stub provider.";
        public int CreateCalls, DestroyCalls, ReleaseCalls, ShutdownCalls;
        public NativeAiStatus GetVersion(out NativeAiVersion version) { version = ProviderVersion; return NativeAiStatus.Ok; }
        public NativeAiStatus Initialize(in NativeAiVersion requested, out NativeAiVersion negotiated) { negotiated = ProviderVersion; return requested.Major == ProviderVersion.Major && requested.Minor <= ProviderVersion.Minor ? NativeAiStatus.Ok : NativeAiStatus.SdkMismatch; }
        public NativeAiStatus Shutdown() { ShutdownCalls++; return NativeAiStatus.Ok; }
        public NativeAiStatus GetCapabilities(ref NativeAiCapabilities capabilities) { capabilities.Flags = 15; return NativeAiStatus.Ok; }
        public NativeAiStatus CreateProvider(out IntPtr provider) { CreateCalls++; provider = _provider; return NativeAiStatus.Ok; }
        public NativeAiStatus DestroyProvider(IntPtr provider) { DestroyCalls++; return provider == _provider ? NativeAiStatus.Ok : NativeAiStatus.InvalidHandle; }
        public NativeAiStatus CancelOperation(IntPtr provider) { _logger?.Invoke(1, "cancel", IntPtr.Zero); return CancelStatus; }
        public NativeAiStatus ReleaseResources(IntPtr provider) { ReleaseCalls++; return NativeAiStatus.Ok; }
        public NativeAiStatus GetLastError(IntPtr provider, byte[]? buffer, ref nuint size)
        {
            byte[] value = Encoding.UTF8.GetBytes(LastErrorText + "\0"); if (buffer is null) { size = (nuint)value.Length; return NativeAiStatus.Ok; } Array.Copy(value, buffer, value.Length); size = (nuint)value.Length; return NativeAiStatus.Ok;
        }
        public NativeAiStatus RegisterLogger(IntPtr provider, NativeAiLogger callback, IntPtr context) { _logger = callback; callback(0, "bridge loaded", context); return NativeAiStatus.Ok; }
        public NativeAiStatus RegisterProgress(IntPtr provider, NativeAiProgress callback, IntPtr context) { _progress = callback; callback(.5, "initialization", context); return NativeAiStatus.Ok; }
        public NativeAiStatus ProcessImage(IntPtr provider, in NativeAiImage input, ref NativeAiImage output) => NativeAiStatus.NotImplemented;
        public void Dispose() { }
    }
}
