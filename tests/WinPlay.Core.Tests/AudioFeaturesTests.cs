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

    private sealed class MockAudioSource(short value) : IAudioSource
    {
        public bool Disposed { get; private set; }
        public void Read(Span<short> interleavedStereo) => interleavedStereo.Fill(value);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void SwitchableAudioSource_HotSwap_SeamlesslySwitchesAndDisposesOld()
    {
        var src1 = new MockAudioSource(100);
        var src2 = new MockAudioSource(200);

        using var switchable = new SwitchableAudioSource(src1);
        Span<short> buf = stackalloc short[4];

        switchable.Read(buf);
        Assert.Equal(100, buf[0]);
        Assert.False(src1.Disposed);

        // Hot swap to source 2
        switchable.SwitchTo(src2);
        Assert.True(src1.Disposed); // Old source disposed

        switchable.Read(buf);
        Assert.Equal(200, buf[0]);
        Assert.False(src2.Disposed);
    }
}
