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
        public FfmpegToolSource Source { get; }
        public string? InstallationDirectory { get; }

        public FfmpegToolPaths(string ffmpegPath, string ffprobePath, FfmpegToolSource source = FfmpegToolSource.Unavailable, string? installationDirectory = null)
        {
            FfmpegPath = ffmpegPath;
            FfprobePath = ffprobePath;
            Source = source;
            InstallationDirectory = installationDirectory;
        }
    }

    public enum FfmpegToolSource { Configured, Managed, Legacy, SystemPath, Unavailable, Mixed }

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

            string? configuredFfmpeg = ExistingConfigured(configuredFfmpegPath);
            string? configuredFfprobe = ExistingConfigured(configuredFfprobePath);
            if (configuredFfmpeg is not null && configuredFfprobe is not null)
                return new(configuredFfmpeg, configuredFfprobe, FfmpegToolSource.Configured, CommonDirectory(configuredFfmpeg, configuredFfprobe));

            string managedDirectory = Path.Combine(root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin");
            string managedFfmpeg = Path.Combine(managedDirectory, "ffmpeg.exe"), managedFfprobe = Path.Combine(managedDirectory, "ffprobe.exe");
            if (configuredFfmpeg is null && configuredFfprobe is null && File.Exists(managedFfmpeg) && File.Exists(managedFfprobe))
                return new(managedFfmpeg, managedFfprobe, FfmpegToolSource.Managed, managedDirectory);

            string ffmpeg = configuredFfmpeg ?? ResolveTool(root, "ffmpeg.exe", null);
            string ffprobe = configuredFfprobe ?? ResolveTool(root, "ffprobe.exe", null);
            FfmpegToolSource source = configuredFfmpeg is not null || configuredFfprobe is not null ? FfmpegToolSource.Mixed :
                IsLegacy(root, ffmpeg, ffprobe) ? FfmpegToolSource.Legacy :
                (IsPathTool(ffmpeg) || IsPathTool(ffprobe)) ? FfmpegToolSource.SystemPath : FfmpegToolSource.Unavailable;
            return new(ffmpeg, ffprobe, source, CommonDirectory(ffmpeg, ffprobe));
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

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            return candidates[0];
        }

        private static string? ExistingConfigured(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        }

        private static bool IsLegacy(string root, string ffmpeg, string ffprobe) =>
            IsUnder(root, ffmpeg) || IsUnder(root, ffprobe);

        private static bool IsPathTool(string path) =>
            !Path.IsPathRooted(path) || (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(directory => string.Equals(Path.GetFullPath(Path.Combine(directory, Path.GetFileName(path))), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

        private static bool IsUnder(string root, string path)
        {
            if (!Path.IsPathRooted(path)) return false;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string? CommonDirectory(string first, string second)
        {
            string? a = Path.GetDirectoryName(first), b = Path.GetDirectoryName(second);
            return a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? a : null;
        }
    }
}
