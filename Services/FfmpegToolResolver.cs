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

            bool hasConfiguredPath = !string.IsNullOrWhiteSpace(configuredFfmpegPath) || !string.IsNullOrWhiteSpace(configuredFfprobePath);
            string? configuredFfmpeg = ExistingConfigured(configuredFfmpegPath, "ffmpeg.exe");
            string? configuredFfprobe = ExistingConfigured(configuredFfprobePath, "ffprobe.exe");
            if (hasConfiguredPath)
            {
                string? configuredDirectory = configuredFfmpeg is not null
                    ? Path.GetDirectoryName(configuredFfmpeg)
                    : configuredFfprobe is not null
                        ? Path.GetDirectoryName(configuredFfprobe)
                        : null;
                if (configuredDirectory is not null)
                {
                    string ffmpeg = Path.Combine(configuredDirectory, "ffmpeg.exe");
                    string ffprobe = Path.Combine(configuredDirectory, "ffprobe.exe");
                    if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                        return new(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe), FfmpegToolSource.Configured, configuredDirectory);
                }

                // An explicit configuration must never be completed by a different
                // installation or by PATH. Fail closed until a complete sibling pair
                // is configured or located.
                return new("", "", FfmpegToolSource.Unavailable);
            }

            string managedDirectory = Path.Combine(root, "Programs", "FFmpeg", FfmpegManagedComponents.Release.Version, "bin");
            string managedFfmpeg = Path.Combine(managedDirectory, "ffmpeg.exe"), managedFfprobe = Path.Combine(managedDirectory, "ffprobe.exe");
            if (configuredFfmpeg is null && configuredFfprobe is null && File.Exists(managedFfmpeg) && File.Exists(managedFfprobe))
                return new(managedFfmpeg, managedFfprobe, FfmpegToolSource.Managed, managedDirectory);

            foreach (string directory in new[] { root, Path.Combine(root, "programs"), Path.Combine(root, "Programs") })
            {
                FfmpegToolPaths? pair = ExistingPair(directory, FfmpegToolSource.Legacy);
                if (pair is not null) return pair;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    FfmpegToolPaths? pair = ExistingPair(directory, FfmpegToolSource.SystemPath);
                    if (pair is not null) return pair;
                }
            }

            return new(Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "ffprobe.exe"), FfmpegToolSource.Unavailable);
        }

        private static FfmpegToolPaths? ExistingPair(string directory, FfmpegToolSource source)
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;
            string ffmpeg = Path.Combine(directory, "ffmpeg.exe"), ffprobe = Path.Combine(directory, "ffprobe.exe");
            return File.Exists(ffmpeg) && File.Exists(ffprobe)
                ? new(Path.GetFullPath(ffmpeg), Path.GetFullPath(ffprobe), source, Path.GetFullPath(directory))
                : null;
        }

        private static string? ExistingConfigured(string? path, string expectedName)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            return File.Exists(expanded) && string.Equals(Path.GetFileName(expanded), expectedName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(expanded)
                : null;
        }

    }
}
