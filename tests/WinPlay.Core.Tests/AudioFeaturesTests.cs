// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

public class AudioFeaturesTests
{
    [Fact]
    public void SystemAudioMover_DualAudio_DefaultsToFalse()
    {
        using var mover = new SystemAudioMover();
        Assert.False(mover.DualAudio);
    }

    [Fact]
    public void SystemAudioMover_EnterStreaming_DualAudioTrue_DoesNotMute()
    {
        using var mover = new SystemAudioMover();
        mover.EnterStreaming(deviceId: null, dualAudio: true);
        Assert.True(mover.DualAudio);
    }

    [Fact]
    public void SystemAudioMover_SetDualAudio_UpdatesProperty()
    {
        using var mover = new SystemAudioMover();
        mover.SetDualAudio(true);
        Assert.True(mover.DualAudio);

        mover.SetDualAudio(false);
        Assert.False(mover.DualAudio);
    }

    [Fact]
    public void SystemAudioMover_CreateCaptureSource_ReturnsNonNull()
    {
        using var mover = new SystemAudioMover();
        using var source = mover.CreateCaptureSource(deviceId: null);
        Assert.NotNull(source);
    }
}
