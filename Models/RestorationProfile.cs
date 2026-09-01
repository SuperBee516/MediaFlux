namespace MediaFlux.Models;

/// <summary>User-facing starting points that populate ordinary Custom restoration settings.</summary>
public enum BuiltInRestorationPreset
{
    ClassicCartoon,
    Anime,
    DvdUpscale,
    VhsCleanup,
    FilmPreservation,
    LiveActionHdCleanup,
    LightCleanup,
    HeavyRestoration,
    AiGeneralEnhancement
}

public sealed record RestorationProfileDocument(int Version, string Name, VideoRestorationSettings Settings);
