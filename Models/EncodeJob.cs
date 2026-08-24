namespace MediaFlux.Models;

public enum EncodeJobScheduleType { Manual, Once }
public enum EncodeJobStatus { Ready, Scheduled, Running, Completed, CompletedWithErrors, Failed, Disabled }

/// <summary>Durable, queue-independent description of an encode run.</summary>
public sealed class EncodeJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New encode job";
    public List<EncodeJobFile> Files { get; set; } = new();
    public EncodeJobSettings Settings { get; set; } = new();
    public EncodeJobScheduleType ScheduleType { get; set; }
    public DateTime? ScheduledLocalTime { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public EncodeJobStatus Status { get; set; } = EncodeJobStatus.Ready;
    public DateTime? LastRunUtc { get; set; }
    public string LastResult { get; set; } = "";
    public long EstimatedOutputBytes { get; set; }
    public long EstimatedSavingsBytes { get; set; }
}

public sealed class EncodeJobFile
{
    public string SourcePath { get; set; } = "";
    public string? CustomCompressionProfile { get; set; }
    public double? CustomTargetMb { get; set; }
}

/// <summary>Only persisted primitive values: changing main-window controls cannot change a saved job.</summary>
public sealed class EncodeJobSettings
{
    public string OutputFolder { get; set; } = "";
    public string CompressionProfile { get; set; } = "";
    public string EncoderId { get; set; } = "";
    public string VideoCodec { get; set; } = "";
    public string EncoderPreset { get; set; } = "";
    public string OutputContainer { get; set; } = "";
    public int QualityValue { get; set; }
    public bool TenBit { get; set; }
    public string AudioChannels { get; set; } = "";
    public string VideoFormat { get; set; } = "";
    public bool AutoTargetSize { get; set; }
    public string TargetSize { get; set; } = "";
    public string Resolution { get; set; } = "";
    public bool DeleteSourceAfterCompression { get; set; }
    public bool EnableOutputSuffix { get; set; }
    public bool EnableCodecSuffix { get; set; }
    public string OutputSuffix { get; set; } = "";

    public EncodeJobSettings Clone() => (EncodeJobSettings)MemberwiseClone();
}
