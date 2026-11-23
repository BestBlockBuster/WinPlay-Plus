// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace WinPlay.Core.Settings;

/// <summary>
/// Persisted configuration options.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool DualAudio { get; set; } = false;
    public string? AudioSourceId { get; set; } = "";
    public string? DisplaySourceId { get; set; } = "";
    public int? DownscaleWidth { get; set; } = null;
    public int? DownscaleHeight { get; set; } = null;
    public bool AspectRatioLocked { get; set; } = true;
    public int MaxFramerate { get; set; } = 60;

    public AppSettings Clone() => new()
    {
        DualAudio = DualAudio,
        AudioSourceId = AudioSourceId,
        DisplaySourceId = DisplaySourceId,
        DownscaleWidth = DownscaleWidth,
        DownscaleHeight = DownscaleHeight,
        AspectRatioLocked = AspectRatioLocked,
        MaxFramerate = MaxFramerate,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.MaxFramerate = SettingsCalculations.ClampFramerate(settings.MaxFramerate);
        if (settings.DownscaleWidth.HasValue)
            settings.DownscaleWidth = Math.Max(2, settings.DownscaleWidth.Value & ~1);
        if (settings.DownscaleHeight.HasValue)
            settings.DownscaleHeight = Math.Max(2, settings.DownscaleHeight.Value & ~1);
        return settings;
    }
}

/// <summary>
/// Helper calculations for settings validation and aspect ratio lock.
/// </summary>
public static class SettingsCalculations
{
    /// <summary>Limits framerate to 24–60 fps. Numbers outside are pulled back to the closest value.</summary>
    public static int ClampFramerate(int fps)
    {
        if (fps < 24) return 24;
        if (fps > 60) return 60;
        return fps;
    }

    /// <summary>
    /// Calculates the counterpart dimension based on aspect ratio, ensuring even integer dimensions.
    /// </summary>
    public static int CalculateAspectResolution(int value, double aspectRatio, bool isWidth)
    {
        if (aspectRatio <= 0.01) aspectRatio = 16.0 / 9.0;
        if (isWidth)
        {
            int h = (int)Math.Round(value / aspectRatio);
            return Math.Max(2, h & ~1);
        }
        else
        {
            int w = (int)Math.Round(value * aspectRatio);
            return Math.Max(2, w & ~1);
        }
    }
}
