// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace WinPlay.Core.Audio;

/// <summary>
/// Moves the PC's audio to AirPlay the way a Mac does: while streaming, it mutes the local
/// default output endpoint (so the speakers are silent and there is no ~2 s echo against the
/// receiver) and captures the system mix via <see cref="ProcessLoopbackCapture"/>, which
/// keeps working even though the endpoint is muted. Explicitly selected endpoints use
/// ordinary endpoint loopback and are never muted. On the last destination stopping, the
/// speakers are restored to their previous state.
///
/// If process-loopback capture is unavailable (older Windows), it falls back to ordinary
/// endpoint loopback and does <em>not</em> mute — audio still streams, just without the
/// local‑mute behaviour.
/// </summary>
public sealed class SystemAudioMover : IDisposable
{
    private readonly object _lock = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _processLoopbackSupported = true;
    private bool _muting;
    private bool _priorMute;
    private string? _mutedDeviceId;

    public bool DualAudio { get; private set; }

    /// <summary>
    /// Mutes the default output endpoint (idempotent) so only the receiver is audible.
    /// Explicit device loopback and <paramref name="dualAudio"/> leave local playback enabled.
    /// </summary>
    public void EnterStreaming(string? deviceId = null, bool dualAudio = false)
    {
        lock (_lock)
        {
            DualAudio = dualAudio;
            if (dualAudio || !string.IsNullOrEmpty(deviceId) || _muting || !_processLoopbackSupported) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device = !string.IsNullOrEmpty(deviceId)
                    ? enumerator.GetDevice(deviceId)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                _priorMute = device.AudioEndpointVolume.Mute;
                _mutedDeviceId = device.ID;
                device.AudioEndpointVolume.Mute = true;
                _muting = true;
            }
            catch (Exception)
            {
                // No default endpoint or no permission — carry on without muting.
            }
        }
    }

    /// <summary>Restores the output endpoint's prior mute state.</summary>
    public void ExitStreaming()
    {
        lock (_lock)
        {
            if (!_muting) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? device = null;
                if (!string.IsNullOrEmpty(_mutedDeviceId))
                {
                    try { device = enumerator.GetDevice(_mutedDeviceId); } catch { }
                }
                device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (device != null)
                {
                    device.AudioEndpointVolume.Mute = _priorMute;
                }
            }
            catch (Exception) { /* endpoint gone; nothing to restore */ }
            _muting = false;
            _mutedDeviceId = null;
        }
    }

    /// <summary>Dynamically updates the Dual Audio mode during active streaming.</summary>
    public void SetDualAudio(bool dualAudio, string? deviceId = null)
    {
        lock (_lock)
        {
            DualAudio = dualAudio;
            if ((dualAudio || !string.IsNullOrEmpty(deviceId)) && _muting)
            {
                ExitStreaming();
            }
            else if (!dualAudio && string.IsNullOrEmpty(deviceId) && !_muting)
            {
                EnterStreaming(deviceId, dualAudio: false);
            }
        }
    }

    /// <summary>
    /// Creates an audio source for a streaming session. Prefers process-loopback capture
    /// for the default device (excludes WinPlay itself, survives the endpoint mute);
    /// uses specific device loopback if a custom device is selected, or falls back to
    /// endpoint loopback.
    /// </summary>
    public IAudioSource CreateCaptureSource(string? deviceId = null)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var dev = enumerator.GetDevice(deviceId);
                if (dev != null)
                {
                    return new LoopbackAudioSource(dev);
                }
            }
            catch (Exception) { /* fallback to default */ }
        }

        if (_processLoopbackSupported)
        {
            try
            {
                return new ProcessLoopbackAudioSource(_ownPid);
            }
            catch (Exception)
            {
                // Older Windows or blocked: stop muting and use endpoint loopback instead.
                lock (_lock) { _processLoopbackSupported = false; }
                ExitStreaming();
            }
        }
        return new LoopbackAudioSource();
    }

    public void Dispose() => ExitStreaming();
}
