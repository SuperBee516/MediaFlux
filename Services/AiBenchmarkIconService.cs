using System.Collections.Concurrent;
using Svg;

namespace MediaFlux.Services;

/// <summary>Loads the small set of vector icons used by the AI Benchmark Manager.</summary>
internal static class AiBenchmarkIconService
{
    private static readonly ConcurrentDictionary<(string Name, int Size), Bitmap> Cache = new();

    public static Bitmap? Get(string fileName, int logicalSize, int dpi)
    {
        int size = Math.Max(1, (int)Math.Round(logicalSize * dpi / 96d));
        Bitmap source = Cache.GetOrAdd((fileName, size), key => Render(key.Name, key.Size));
        return new Bitmap(source);
    }

    private static Bitmap Render(string fileName, int size)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", "ai-benchmark-icons", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("AI Benchmark Manager icon asset was not found.", path);
        SvgDocument document = SvgDocument.Open(path);
        return document.Draw(size, size) ?? throw new InvalidOperationException($"Could not render AI Benchmark Manager icon '{fileName}'.");
    }
}
