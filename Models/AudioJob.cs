using System;

namespace Encode.Models
{
    public enum AudioQuality
    {
        Auto = 0,
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh
    }

    public enum LoudnormMode
    {
        None = 0,
        SinglePass = 1,
        TwoPass = 2
    }

    /// <summary>
    /// Represents a single audio job (extract or convert) for ffmpeg.
    /// </summary>
    public sealed class AudioJob
    {
        /// <summary>Full path to the input file (video or audio).</summary>
        public string InputPath { get; set; } = string.Empty;

        /// <summary>Destination folder for the created audio file.</summary>
        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>
        /// Operation: "Extract" (stream copy) or "Convert".
        /// </summary>
        public string Operation { get; set; } = "Extract";

        /// <summary>
        /// Target audio codec (libmp3lame, aac, flac, etc.).
        /// If null, AudioService will choose a sensible default based on file extension.
        /// </summary>
        public string? Codec { get; set; }

        /// <summary>
        /// File extension for the output file, including leading dot (e.g. ".mp3").
        /// If null, AudioService derives a default.
        /// </summary>
        public string? OutputExtension { get; set; }

        /// <summary>
        /// Target bitrate in kbps for convert jobs.
        /// If null, AudioService will use codec/quality-specific defaults.
        /// </summary>
        public int? BitrateKbps { get; set; }

        /// <summary>
        /// User-selected quality preset (Auto / VeryLow / Low / Medium / High / VeryHigh).
        /// </summary>
        public AudioQuality Quality { get; set; } = AudioQuality.Auto;

        /// <summary>
        /// Loudness normalization mode (None, SinglePass, TwoPass).
        /// </summary>
        public LoudnormMode Loudnorm { get; set; } = LoudnormMode.None;

        /// <summary>
        /// When using two-pass loudnorm, this holds the fully-expanded loudnorm filter string.
        /// </summary>
        public string? LoudnormFilterOverride { get; set; }

        /// <summary>
        /// Whether to apply RNNoise-based denoising using the arnndn filter.
        /// </summary>
        public bool DenoiseEnabled { get; set; }

        /// <summary>
        /// Path to the RNNoise ONNX model to use with arnndn.
        /// If null or empty, DenoiseEnabled has no effect.
        /// </summary>
        public string? DenoiseModelPath { get; set; }
    }
}
