using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MediaFlux.Services;

public sealed record HardwareSnapshot(
    string Cpu, int LogicalCores, string Gpu, string GpuDriver, long? DedicatedVramBytes,
    long? InstalledRamBytes, string SourceDrive, string TempDrive, string OutputDrive,
    string WindowsVersion, string FfmpegVersion, string? AiBackend = null);

public readonly record struct HardwareUsageSample(
    double? GpuPercent, long? VramUsedBytes, double? CpuPercent,
    double? DiskReadBytesPerSecond, double? DiskWriteBytesPerSecond);

/// <summary>Best-effort Windows hardware discovery and low-frequency process correlation.</summary>
public sealed class HardwarePerformanceService : IDisposable
{
    private readonly WindowsEncodingSystemTelemetryProvider _telemetry = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private long _previousRead, _previousWrite, _previousTimestamp;

    public static HardwareSnapshot Capture(string sourcePath, string tempPath, string outputPath, string ffmpegPath)
    {
        (string gpu, string driver, long? vram) = DiscoverNvidiaGpu();
        return new HardwareSnapshot(
            CpuName(), Environment.ProcessorCount, gpu, driver, vram, InstalledRamBytes(),
            Drive(sourcePath), Drive(tempPath), Drive(outputPath), RuntimeInformation.OSDescription,
            FileVersion(ffmpegPath));
    }

    public static long? DetectDedicatedGpuVramBytes() => DiscoverNvidiaGpu().Vram;
    public static string DetectGpuIdentity() => DiscoverNvidiaGpu().Name;

    public HardwareUsageSample Sample()
    {
        EncodingSystemTelemetry telemetry = _telemetry.Sample();
        double? read = null, write = null;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (GetProcessIoCounters(_process.Handle, out IoCounters counters) && _previousTimestamp != 0)
            {
                double seconds = (now - _previousTimestamp) / (double)Stopwatch.Frequency;
                if (seconds > 0)
                {
                    read = Math.Max(0, (long)counters.ReadTransferCount - _previousRead) / seconds;
                    write = Math.Max(0, (long)counters.WriteTransferCount - _previousWrite) / seconds;
                }
            }
            if (GetProcessIoCounters(_process.Handle, out counters))
            {
                _previousRead = (long)counters.ReadTransferCount;
                _previousWrite = (long)counters.WriteTransferCount;
            }
            _previousTimestamp = now;
        }
        catch { }
        return new(telemetry.GpuPercent, telemetry.VramUsedBytes, telemetry.SystemCpuPercent, read, write);
    }

    public void Dispose()
    {
        _telemetry.Dispose();
        _process.Dispose();
    }

    private static string CpuName()
    {
        try { return Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Unknown")?.ToString() ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    private static long? InstalledRamBytes()
    {
        try { var status = new MemoryStatusEx(); return GlobalMemoryStatusEx(status) ? (long)Math.Min(status.TotalPhysical, long.MaxValue) : null; }
        catch { return null; }
    }

    private static string Drive(string path)
    {
        try { DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(path))!); return $"{drive.Name} ({drive.DriveFormat}, {drive.TotalSize / 1073741824d:0.#} GiB)"; }
        catch { return "Unavailable"; }
    }

    private static string FileVersion(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Unknown" : "Unavailable"; }
        catch { return "Unavailable"; }
    }

    private static (string Name, string Driver, long? Vram) DiscoverNvidiaGpu()
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "nvidia-smi.exe", Arguments = "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } };
            if (!process.Start() || !process.WaitForExit(1000) || process.ExitCode != 0) return ("Unavailable", "Unavailable", null);
            string[] values = (process.StandardOutput.ReadLine() ?? "").Split(',').Select(value => value.Trim()).ToArray();
            long? vram = values.Length > 2 && long.TryParse(values[2], out long mib) ? mib * 1048576 : null;
            return (values.ElementAtOrDefault(0) ?? "Unavailable", values.ElementAtOrDefault(1) ?? "Unavailable", vram);
        }
        catch { return ("Unavailable", "Unavailable", null); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx { public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>(); public uint Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

/// <summary>Samples only while AI restoration runs; it retains aggregate statistics in the timing service.</summary>
public sealed class HardwarePerformanceSampler : IDisposable
{
    private readonly PerformanceTimingService _timing;
    private readonly HardwarePerformanceService _hardware;
    private readonly Action<HardwareUsageSample>? _sampleObserver;
    private readonly System.Threading.Timer _timer;
    private int _disposed;

    public HardwarePerformanceSampler(PerformanceTimingService timing, TimeSpan? interval = null, HardwarePerformanceService? hardware = null, Action<HardwareUsageSample>? sampleObserver = null)
    {
        _timing = timing;
        _hardware = hardware ?? new HardwarePerformanceService();
        _sampleObserver = sampleObserver;
        TimeSpan period = interval ?? TimeSpan.FromSeconds(3);
        _timer = new System.Threading.Timer(_ => SampleNow(), null, period, period);
    }

    public void SampleNow()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        HardwareUsageSample sample = _hardware.Sample();
        _timing.RecordHardwareSample(sample);
        _sampleObserver?.Invoke(sample);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        _hardware.Dispose();
    }
}
