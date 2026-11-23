// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Settings;
using Xunit;

namespace WinPlay.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void AppSettings_Defaults_MatchSpecification()
    {
        var settings = new AppSettings();

        Assert.False(settings.DualAudio);
        Assert.Equal("", settings.AudioSourceId);
        Assert.Equal("", settings.DisplaySourceId);
        Assert.Null(settings.DownscaleWidth);
        Assert.Null(settings.DownscaleHeight);
        Assert.True(settings.AspectRatioLocked);
        Assert.Equal(60, settings.MaxFramerate);
    }

    [Theory]
    [InlineData(10, 24)]
    [InlineData(23, 24)]
    [InlineData(24, 24)]
    [InlineData(30, 30)]
    [InlineData(50, 50)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(120, 60)]
    [InlineData(-5, 24)]
    public void Framerate_ClampsTo_24_Through_60(int input, int expected)
    {
        int actual = SettingsCalculations.ClampFramerate(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1920, 16.0 / 9.0, 1080)]
    [InlineData(1280, 16.0 / 9.0, 720)]
    [InlineData(1600, 16.0 / 9.0, 900)]
    [InlineData(3840, 16.0 / 9.0, 2160)]
    public void AspectResolution_WidthToHeight_CalculatesEvenDimensions(int width, double ratio, int expectedHeight)
    {
        int h = SettingsCalculations.CalculateAspectResolution(width, ratio, isWidth: true);
        Assert.Equal(expectedHeight, h);
        Assert.True(h % 2 == 0, "Height must be even for H.264 / NV12");
    }

    [Theory]
    [InlineData(1080, 16.0 / 9.0, 1920)]
    [InlineData(720, 16.0 / 9.0, 1280)]
    [InlineData(900, 16.0 / 9.0, 1600)]
    [InlineData(2160, 16.0 / 9.0, 3840)]
    public void AspectResolution_HeightToWidth_CalculatesEvenDimensions(int height, double ratio, int expectedWidth)
    {
        int w = SettingsCalculations.CalculateAspectResolution(height, ratio, isWidth: false);
        Assert.Equal(expectedWidth, w);
        Assert.True(w % 2 == 0, "Width must be even for H.264 / NV12");
    }

    [Fact]
    public void AppSettings_Serialization_RoundTripsAccurately()
    {
        var original = new AppSettings
        {
            DualAudio = true,
            AudioSourceId = "test-audio-id",
            DisplaySourceId = "\\\\.\\DISPLAY2",
            DownscaleWidth = 1280,
            DownscaleHeight = 720,
            AspectRatioLocked = false,
            MaxFramerate = 45,
        };

        string json = original.ToJson();
        var loaded = AppSettings.FromJson(json);

        Assert.Equal(original.DualAudio, loaded.DualAudio);
        Assert.Equal(original.AudioSourceId, loaded.AudioSourceId);
        Assert.Equal(original.DisplaySourceId, loaded.DisplaySourceId);
        Assert.Equal(original.DownscaleWidth, loaded.DownscaleWidth);
        Assert.Equal(original.DownscaleHeight, loaded.DownscaleHeight);
        Assert.Equal(original.AspectRatioLocked, loaded.AspectRatioLocked);
        Assert.Equal(original.MaxFramerate, loaded.MaxFramerate);
    }

    [Fact]
    public void AppSettings_FromJson_ClampsInvalidValues()
    {
        string json = """
        {
            "DualAudio": true,
            "MaxFramerate": 144,
            "DownscaleWidth": 1281,
            "DownscaleHeight": 721
        }
        """;

        var loaded = AppSettings.FromJson(json);

        // Framerate pulled back to 60
        Assert.Equal(60, loaded.MaxFramerate);
        // Dimensions rounded to even numbers
        Assert.Equal(1280, loaded.DownscaleWidth);
        Assert.Equal(720, loaded.DownscaleHeight);
    }
}
