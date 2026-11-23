// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// An <see cref="IAudioSource"/> wrapper that allows seamlessly hot-swapping the underlying
/// capture source at runtime without interrupting active streaming sessions.
/// </summary>
public sealed class SwitchableAudioSource : IAudioSource
{
    private readonly object _lock = new();
    private IAudioSource _inner;
    private bool _disposed;

    public SwitchableAudioSource(IAudioSource initial)
    {
        _inner = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>Hot-swaps the underlying audio source, disposing the old source.</summary>
    public void SwitchTo(IAudioSource newSource)
    {
        ArgumentNullException.ThrowIfNull(newSource);
        IAudioSource old;
        lock (_lock)
        {
            if (_disposed)
            {
                newSource.Dispose();
                return;
            }
            old = _inner;
            _inner = newSource;
        }
        old.Dispose();
    }

    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                interleavedStereo.Clear();
                return;
            }
            _inner.Read(interleavedStereo);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
        }
    }
}
