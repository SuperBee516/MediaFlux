namespace MediaFlux.Models;

public enum RestorationPreviewSelectionMode { NoRestoration, CurrentSettings, Recommended }

public sealed record VideoRestorationPreviewAnalysisUpdate(VideoRestorationAnalysisResult Analysis, VideoRestorationRecommendation Recommendation);

/// <summary>Monotonic request identity used to keep obsolete asynchronous preview work from updating the UI.</summary>
public sealed class VideoRestorationPreviewOperationGate
{
    private long _current;
    public long Begin() => Interlocked.Increment(ref _current);
    public void Invalidate() => Interlocked.Increment(ref _current);
    public bool IsCurrent(long request) => request == Interlocked.Read(ref _current);
}

/// <summary>Selection state used by the restoration preview. It is deliberately separate from persisted encode settings.</summary>
public sealed class VideoRestorationPreviewSelection
{
    public VideoRestorationPreviewSelection(VideoRestorationSettings encodeSettings, VideoRestorationRecommendation? recommendation = null)
    {
        EncodeSettings = encodeSettings.Clone();
        PreviewSettings = encodeSettings.Clone();
        Recommendation = recommendation;
    }

    public VideoRestorationSettings EncodeSettings { get; private set; }
    public VideoRestorationSettings PreviewSettings { get; private set; }
    public VideoRestorationRecommendation? Recommendation { get; private set; }
    public RestorationPreviewSelectionMode Mode { get; private set; } = RestorationPreviewSelectionMode.CurrentSettings;
    public bool DiffersFromEncode => !Equivalent(EncodeSettings, PreviewSettings);

    public void PreviewOff() => SelectMode(RestorationPreviewSelectionMode.NoRestoration);
    public void PreviewCurrent() => SelectMode(RestorationPreviewSelectionMode.CurrentSettings);
    public bool PreviewRecommendation()
    {
        if (Recommendation == null) return false;
        SelectMode(RestorationPreviewSelectionMode.Recommended);
        return true;
    }

    public bool SelectMode(RestorationPreviewSelectionMode mode)
    {
        if (mode == RestorationPreviewSelectionMode.Recommended && Recommendation == null) return false;
        Mode = mode;
        PreviewSettings = mode switch
        {
            RestorationPreviewSelectionMode.NoRestoration => new VideoRestorationSettings(),
            RestorationPreviewSelectionMode.Recommended => Recommendation!.Settings.Clone(),
            _ => EncodeSettings.Clone()
        };
        return true;
    }
    public void UsePreviewSettings(VideoRestorationSettings settings)
    {
        PreviewSettings = settings.Clone();
        Mode = RestorationPreviewSelectionMode.CurrentSettings;
    }

    public void SetRecommendation(VideoRestorationRecommendation? recommendation)
    {
        Recommendation = recommendation;
        if (Mode == RestorationPreviewSelectionMode.Recommended && recommendation == null)
            PreviewCurrent();
    }

    /// <summary>Returns a clone for the caller to persist only after an explicit user action.</summary>
    public VideoRestorationSettings ApplyToEncodeSettings()
    {
        EncodeSettings = PreviewSettings.Clone();
        Mode = RestorationPreviewSelectionMode.CurrentSettings;
        return EncodeSettings.Clone();
    }

    public static bool Equivalent(VideoRestorationSettings left, VideoRestorationSettings right) =>
        left.Preset == right.Preset && left.Denoise == right.Denoise && left.Deblock == right.Deblock &&
        left.Deband == right.Deband && left.Sharpen == right.Sharpen && left.Deinterlace == right.Deinterlace &&
        left.Brightness == right.Brightness && left.Contrast == right.Contrast && left.Saturation == right.Saturation &&
        left.Resize == right.Resize && left.CustomWidth == right.CustomWidth && left.CustomHeight == right.CustomHeight &&
        left.PreserveAspectRatio == right.PreserveAspectRatio && left.AiMode == right.AiMode && left.AiModelId == right.AiModelId &&
        left.AiScale == right.AiScale && left.AiDevice == right.AiDevice && left.AiBackendPath == right.AiBackendPath && left.AiModelsDirectory == right.AiModelsDirectory;
}
