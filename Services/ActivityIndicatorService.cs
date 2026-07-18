using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace MediaFlux.Services
{
    public enum UiActivity
    {
        None = 0,
        Encoding = 10,
        FolderScan = 20,
        Upscaling = 30
    }

    public sealed class ActivityVisualConfig
    {
        public string? AnimatedImagePath { get; init; }
        public string? IdleImagePath { get; init; }
        public int Priority { get; init; } = 0;
        public string? StatusText { get; init; }
    }

    // JSON model: what we deserialize from activity-indicator.json
    public sealed class ActivityIndicatorConfigEntry
    {
        public string? Animated { get; set; }
        public string? Idle { get; set; }
        public int? Priority { get; set; }
        public string? StatusText { get; set; }
    }

    public sealed class ActivityIndicatorConfigRoot
    {
        public Dictionary<string, ActivityIndicatorConfigEntry>? Activities { get; set; }
    }

    public static class ActivityIndicatorConfigLoader
    {
        private const string ConfigFileName = "activity-indicator.json";

        public static Dictionary<UiActivity, ActivityVisualConfig> Load(string basePath)
        {
            // Start with hard-coded defaults so the app works even with no JSON.
            var defaults = GetDefaultConfig();

            string path = Path.Combine(basePath, ConfigFileName);
            if (!File.Exists(path))
                return defaults;

            try
            {
                string json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<ActivityIndicatorConfigRoot>(json);

                if (root?.Activities == null || root.Activities.Count == 0)
                    return defaults;

                // Clone defaults so we can override selectively
                var merged = new Dictionary<UiActivity, ActivityVisualConfig>(defaults);

                foreach (var kvp in root.Activities)
                {
                    if (!Enum.TryParse<UiActivity>(kvp.Key, ignoreCase: true, out var activity))
                        continue;

                    var entry = kvp.Value;
                    if (entry == null)
                        continue;

                    // Get any existing defaults for this activity
                    merged.TryGetValue(activity, out var prior);

                    merged[activity] = new ActivityVisualConfig
                    {
                        AnimatedImagePath = entry.Animated ?? prior?.AnimatedImagePath,
                        IdleImagePath = entry.Idle ?? prior?.IdleImagePath,
                        Priority = entry.Priority ?? prior?.Priority ?? 0,
                        StatusText = entry.StatusText ?? prior?.StatusText
                    };
                }

                return merged;
            }
            catch
            {
                // On any error, just fall back to defaults
                return defaults;
            }
        }

        private static Dictionary<UiActivity, ActivityVisualConfig> GetDefaultConfig()
        {
            // These are "safe" defaults. You can rename files as you see fit.
            return new Dictionary<UiActivity, ActivityVisualConfig>
            {
                {
                    UiActivity.None,
                    new ActivityVisualConfig
                    {
                        IdleImagePath = Path.Combine("images", "gear-idle.png"),    // static idle image
                        Priority = 0,
                        StatusText = null                   // no text when idle
                    }
                },
                {
                    UiActivity.Encoding,
                    new ActivityVisualConfig
                    {
                        AnimatedImagePath = Path.Combine("images", "gear-wheel-animated.gif"),
                        IdleImagePath = "gear-idle.png",
                        Priority = 100,
                        StatusText = null
                    }
                },
                {
                    UiActivity.FolderScan,
                    new ActivityVisualConfig
                    {
                        AnimatedImagePath = Path.Combine("images", "folder-scan-animated.gif"),
                        IdleImagePath = "gear-idle.png",
                        Priority = 90,
                        StatusText = null
                    }
                },
                {
                    UiActivity.Upscaling,
                    new ActivityVisualConfig
                    {
                        AnimatedImagePath = Path.Combine("images", "upscale-animated.gif"),
                        IdleImagePath = "gear-idle.png",
                        Priority = 95,
                        StatusText = null
                    }
                }
            };
        }
    }

    /// <summary>
    /// Centralized controller for the "activity" indicator (spinner + optional label).
    /// You call StartActivity/StopActivity; it decides which image and text to show.
    /// </summary>
    public sealed class ActivityIndicatorService : IDisposable
    {
        private readonly Control _uiContext;
        private readonly PictureBox _pictureBox;
        private readonly Label? _statusLabel;
        private readonly string _basePath;
        private readonly Dictionary<UiActivity, ActivityVisualConfig> _config;
        private readonly HashSet<UiActivity> _active = new();

        private UiActivity _currentShown = (UiActivity)(-1); // sentinel so first UpdateVisual always applies image
        private bool _disposed;

        public ActivityIndicatorService(
            Control uiContext,
            PictureBox pictureBox,
            Label? statusLabel,
            string basePath,
            Dictionary<UiActivity, ActivityVisualConfig> config)
        {
            _uiContext = uiContext ?? throw new ArgumentNullException(nameof(uiContext));
            _pictureBox = pictureBox ?? throw new ArgumentNullException(nameof(pictureBox));
            _statusLabel = statusLabel;
            _basePath = basePath ?? AppDomain.CurrentDomain.BaseDirectory;
            _config = config ?? throw new ArgumentNullException(nameof(config));

            UpdateVisual();
        }

        public void StartActivity(UiActivity activity)
        {
            if (_disposed) return;

            RunOnUiThread(() =>
            {
                _active.Add(activity);
                UpdateVisual();
            });
        }

        public void StopActivity(UiActivity activity)
        {
            if (_disposed) return;

            RunOnUiThread(() =>
            {
                _active.Remove(activity);
                UpdateVisual();
            });
        }

        private void UpdateVisual()
        {
            if (_disposed) return;

            UiActivity toShow;
            if (_active.Count == 0)
            {
                // Show idle state
                toShow = UiActivity.None;
            }
            else
            {
                // Choose highest-priority active activity
                toShow = _active
                    .Select(a => new { Activity = a, Cfg = GetConfig(a) })
                    .OrderByDescending(x => x.Cfg?.Priority ?? 0)
                    .Select(x => x.Activity)
                    .FirstOrDefault();
            }

            if (toShow == _currentShown)
                return;

            _currentShown = toShow;

            var cfg = GetConfig(toShow);

            bool isIdle = (toShow == UiActivity.None);
            if (isIdle)
            {
                ApplyImage(null);
                ApplyStatusText(null);
                return;
            }

            string? imagePath = null;
            string? statusText = null;

            if (cfg != null)
            {
                imagePath = cfg.AnimatedImagePath ?? cfg.IdleImagePath;
                statusText = cfg.StatusText;
            }

            ApplyImage(imagePath);
            ApplyStatusText(statusText);
        }

        private ActivityVisualConfig? GetConfig(UiActivity activity)
        {
            _config.TryGetValue(activity, out var cfg);
            return cfg;
        }

        private void ApplyImage(string? relPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relPath))
                {
                    _pictureBox.Visible = false;
                    var oldImg = _pictureBox.Image;
                    _pictureBox.Image = null;
                    oldImg?.Dispose();
                    return;
                }

                string fullPath = Path.IsPathRooted(relPath)
                    ? relPath
                    : Path.Combine(_basePath, relPath);

                if (!File.Exists(fullPath))
                {
                    _pictureBox.Visible = false;
                    var oldImg = _pictureBox.Image;
                    _pictureBox.Image = null;
                    oldImg?.Dispose();
                    return;
                }

                var old = _pictureBox.Image;
                _pictureBox.Image = Image.FromFile(fullPath);
                old?.Dispose();

                _pictureBox.Visible = true;
            }
            catch
            {
                _pictureBox.Visible = false;
                var old = _pictureBox.Image;
                _pictureBox.Image = null;
                old?.Dispose();
            }
        }

        private void ApplyStatusText(string? text)
        {
            if (_statusLabel == null)
                return;

            if (string.IsNullOrWhiteSpace(text))
            {
                _statusLabel.Visible = false;
                _statusLabel.Text = string.Empty;
            }
            else
            {
                _statusLabel.Text = text;
                _statusLabel.Visible = true;
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (_uiContext.IsDisposed) return;

            if (_uiContext.InvokeRequired)
            {
                try { _uiContext.BeginInvoke(action); } catch { }
            }
            else
            {
                action();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                var img = _pictureBox.Image;
                _pictureBox.Image = null;
                img?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
