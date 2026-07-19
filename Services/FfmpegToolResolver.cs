using System;
using System.IO;

namespace MediaFlux.Services
{
    public sealed class FfmpegToolPaths
    {
        public string FfmpegPath { get; }
        public string FfprobePath { get; }
        public bool HasFfmpeg => File.Exists(FfmpegPath);
        public bool HasFfprobe => File.Exists(FfprobePath);
        public bool AreAllAvailable => HasFfmpeg && HasFfprobe;

        public FfmpegToolPaths(string ffmpegPath, string ffprobePath)
        {
            FfmpegPath = ffmpegPath;
            FfprobePath = ffprobePath;
        }
    }

    public static class FfmpegToolResolver
    {
        public static FfmpegToolPaths Resolve(
            string baseDirectory,
            string? configuredFfmpegPath = null,
            string? configuredFfprobePath = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            return new FfmpegToolPaths(
                ResolveTool(root, "ffmpeg.exe", configuredFfmpegPath),
                ResolveTool(root, "ffprobe.exe", configuredFfprobePath));
        }

        private static string ResolveTool(string root, string fileName, string? configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
                if (File.Exists(expanded))
                    return expanded;
            }

            var candidates = new[]
            {
                Path.Combine(root, fileName),
                Path.Combine(root, "programs", fileName),
                Path.Combine(root, "Programs", fileName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return candidates[0];
        }
    }
}
