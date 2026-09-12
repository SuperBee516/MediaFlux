namespace MediaFlux.Services;

/// <summary>Provides the shared semantic contract for audio and subtitle language tags.</summary>
internal static class LanguageMetadataNormalizer
{
    /// <summary>Returns an empty string for unspecified language and a lower-case trimmed tag otherwise.</summary>
    internal static string Normalize(string? language)
    {
        string value = language?.Trim() ?? "";
        return value.Length == 0 || value.Equals("und", StringComparison.OrdinalIgnoreCase)
            ? ""
            : value.ToLowerInvariant();
    }

    internal static bool Equivalent(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
