// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Sockets;
using WinPlay.Capture;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Hap;
using WinPlay.Core.Mirror;
using WinPlay.Core.Raop;
using WinPlay.Core.Settings;

namespace WinPlay.App.Services;

/// <summary>
/// Owns the active streaming sessions — one coordinated <see cref="GroupSession"/> per
/// audio destination and one <see cref="MirrorSession"/> per mirroring destination.
///
/// Each destination is a self-contained lifecycle guarded by its own cancellation token:
/// starting connects and streams; a dropped connection reconnects with backoff; and
/// <see cref="StopAudioAsync"/> cancels that token so a stop is <em>authoritative</em> —
/// no in-flight reconnect can bring a torn-down destination back to life.
/// </summary>
public sealed class StreamController : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PinEntryTimeout = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<string, AudioDestination> _audio = new();
    private readonly ConcurrentDictionary<string, MirrorDestination> _mirrors = new();
    private readonly CredentialStore _credentials = new();
    private readonly SystemAudioMover _mover = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DiagnosticsLog _diagnostics = new();
    private readonly object _audioSourceSwitchLock = new();
    private Task _audioSourceSwitchTask = Task.CompletedTask;
    private string? _lastAudioSourceId;
    private long _audioSourceRevision;

    public event Action<string, string>? SessionStage;   // key, message
    public event Action<string, Exception>? SessionFailed;

    /// <summary>Rolling in-memory diagnostics log of recent session events (for a status view).</summary>
    public DiagnosticsLog Diagnostics => _diagnostics;

    public StreamController()
    {
        _lastAudioSourceId = SettingsService.Instance.Current.AudioSourceId;
        SessionStage += (key, msg) => _diagnostics.Add(key, msg);
        SessionFailed += (key, ex) => _diagnostics.Add(key, $"failed: {ex.Message}");
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        string? previousAudioSourceId = Interlocked.Exchange(ref _lastAudioSourceId, settings.AudioSourceId);
        bool audioSourceChanged = !string.Equals(
            previousAudioSourceId, settings.AudioSourceId, StringComparison.OrdinalIgnoreCase);

        if (!_audio.IsEmpty || !_mirrors.IsEmpty)
        {
            _mover.SetDualAudio(settings.DualAudio, settings.AudioSourceId);

            foreach (var mirror in _mirrors.Values)
            {
                // Dynamically update display source on active mirror
                var (adapterIdx, outputIdx) = SettingsService.Instance.GetDisplayIndices(settings.DisplaySourceId);
                mirror.Source.UpdateConfiguration(adapterIdx, outputIdx);
            }
        }

        if (audioSourceChanged)
        {
            long revision = Interlocked.Increment(ref _audioSourceRevision);
            lock (_audioSourceSwitchLock)
            {
                Task previous = _audioSourceSwitchTask;
                _audioSourceSwitchTask = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    await SwitchAudioSourcesAsync(settings.AudioSourceId, revision).ConfigureAwait(false);
                });
            }
        }
    }

    private async Task SwitchAudioSourcesAsync(string? deviceId, long revision)
    {
        if (revision != Volatile.Read(ref _audioSourceRevision)) return;

        foreach (var dest in _audio.Values)
        {
            await SwitchAudioSourceAsync(dest.Entry.Key, () => dest.AudioSource, deviceId, revision)
                .ConfigureAwait(false);
        }

        foreach (var (key, mirror) in _mirrors)
        {
            await SwitchAudioSourceAsync(key, () => mirror.AudioSource, deviceId, revision)
                .ConfigureAwait(false);
        }
    }

    private async Task SwitchAudioSourceAsync(string key, Func<SwitchableAudioSource?> getSource,
        string? deviceId, long revision)
    {
        if (getSource() is null) return;

        try
        {
            var newSource = await Task.Run(() => _mover.CreateCaptureSource(deviceId), _lifetime.Token)
                .ConfigureAwait(false);
            if (_lifetime.IsCancellationRequested || revision != Volatile.Read(ref _audioSourceRevision))
            {
                newSource.Dispose();
                return;
            }

            var switchable = getSource();
            if (switchable is null)
                newSource.Dispose();
            else
                switchable.SwitchTo(newSource);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SessionFailed?.Invoke(key, ex);
        }
    }

    /// <summary>
    /// Prompts the user for the PIN a receiver is displaying (argument: receiver name).
    /// Return null to cancel. When unset, PIN-protected receivers simply fail.
    /// </summary>
    public Func<string, Task<string?>>? PinPrompt { get; set; }

    public bool IsAudioActive(string key) => _audio.ContainsKey(key);
    public bool IsMirroring(string key) => _mirrors.ContainsKey(key);
    public int ActiveCount => _audio.Count + _mirrors.Count;

    // ------------------------------------------------------------ audio

    private sealed class AudioDestination(PickerEntry entry, double volumeDb, CancellationTokenSource cts)
    {
        public PickerEntry Entry { get; } = entry;
        public double VolumeDb { get; set; } = volumeDb;
        public CancellationTokenSource Cts { get; } = cts;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public GroupSession? Session { get; set; }
        public SwitchableAudioSource? AudioSource { get; set; }
        public volatile bool Stopped;
    }

    public async Task StartAudioAsync(PickerEntry entry, double volumeDb, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
        var dest = new AudioDestination(entry, volumeDb, cts);
        if (!_audio.TryAdd(entry.Key, dest))
        {
            cts.Dispose();
            return; // already active
        }

        try
        {
            var members = ResolveMembers(entry);
            var session = await ConnectAudioAsync(entry, members, dest.Cts.Token).ConfigureAwait(false);
            dest.Session = session;
            session.Faulted += _ => OnAudioFaulted(dest);

            // "Move" the audio like AirPlay on a Mac: capture the system mix and mute the
            // local speakers so only the receiver plays (avoids a ~2 s echo), unless Dual Audio is enabled.
            var settings = SettingsService.Instance.Current;
            _mover.EnterStreaming(settings.AudioSourceId, settings.DualAudio);
            var switchable = new SwitchableAudioSource(_mover.CreateCaptureSource(settings.AudioSourceId));
            dest.AudioSource = switchable;
            await session.StartStreamingAsync(switchable, volumeDb).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SessionFailed?.Invoke(entry.Key, ex);
            await StopAudioAsync(entry.Key).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<GroupSession> ConnectAudioAsync(PickerEntry entry,
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct)
    {
        try
        {
            return await ConnectAsync(entry, members, ct).ConfigureAwait(false);
        }
        catch (AggregateException ex) when (
            PinPrompt is not null && ex.InnerExceptions.OfType<PairingRequiredException>().Any())
        {
            await PairLeaderAsync(entry, ct).ConfigureAwait(false);
            return await ConnectAsync(entry, ResolveMembers(entry), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Handles a dropped connection: reconnect with backoff unless the destination was stopped.</summary>
    private void OnAudioFaulted(AudioDestination dest)
    {
        if (dest.Stopped || dest.Cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            await dest.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (dest.Stopped || dest.Cts.IsCancellationRequested) return;
                if (dest.Session is { } dead) { dest.Session = null; await dead.DisposeAsync().ConfigureAwait(false); }

                TimeSpan delay = TimeSpan.FromSeconds(2);
                while (!dest.Stopped && !dest.Cts.IsCancellationRequested)
                {
                    SessionStage?.Invoke(dest.Entry.Key, $"connection lost — reconnecting in {delay.TotalSeconds:F0}s");
                    try { await Task.Delay(delay, dest.Cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try
                    {
                        var members = ResolveMembers(dest.Entry);
                        var session = await ConnectAudioAsync(dest.Entry, members, dest.Cts.Token).ConfigureAwait(false);
                        if (dest.Stopped || dest.Cts.IsCancellationRequested)
                        {
                            await session.DisposeAsync().ConfigureAwait(false);
                            return;
                        }
                        dest.Session = session;
                        session.Faulted += _ => OnAudioFaulted(dest);
                        var settings = SettingsService.Instance.Current;
                        var switchable = new SwitchableAudioSource(_mover.CreateCaptureSource(settings.AudioSourceId));
                        dest.AudioSource = switchable;
                        await session.StartStreamingAsync(switchable, dest.VolumeDb).ConfigureAwait(false);
                        SessionStage?.Invoke(dest.Entry.Key, "reconnected");
                        return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        SessionStage?.Invoke(dest.Entry.Key, $"reconnect failed: {ex.Message}");
                        delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                    }
                }
            }
            finally { dest.Gate.Release(); }
        });
    }

    public async Task StopAudioAsync(string key)
    {
        if (!_audio.TryRemove(key, out var dest)) return;
        dest.Stopped = true;
        dest.Cts.Cancel();                                  // kills any in-flight reconnect
        await dest.Gate.WaitAsync().ConfigureAwait(false);  // wait out a reconnect mid-flight
        try
        {
            if (dest.Session is { } s) { dest.Session = null; await s.DisposeAsync().ConfigureAwait(false); }
        }
        finally
        {
            dest.Gate.Release();
            dest.Cts.Dispose();
        }
        // Restore local speakers once nothing (audio or mirror) is streaming.
        if (_audio.IsEmpty && _mirrors.IsEmpty) _mover.ExitStreaming();
    }

    public async Task SetVolumeAsync(string key, double volumeDb)
    {
        if (_audio.TryGetValue(key, out var dest))
        {
            dest.VolumeDb = volumeDb;
            if (dest.Session is { } s)
            {
                try { await s.SetVolumeAsync(volumeDb).ConfigureAwait(false); }
                catch (Exception ex) { SessionFailed?.Invoke(key, ex); }
            }
        }
    }

    /// <summary>Pushes now-playing metadata + optional artwork to every active audio destination.</summary>
    public async Task PushNowPlayingAsync(string? title, string? artist, string? album, byte[]? artwork)
    {
        foreach (var dest in _audio.Values)
        {
            if (dest.Session is not { } s) continue;
            try
            {
                await s.SendMetadataAsync(title, artist, album).ConfigureAwait(false);
                if (artwork is { Length: > 0 })
                    await s.SendArtworkAsync(artwork).ConfigureAwait(false);
            }
            catch (Exception) { /* metadata is best-effort */ }
        }
    }

    // ------------------------------------------------------------ screen mirroring

    private sealed record MirrorDestination(MirrorSession Session, ScreenMirrorSource Source, SwitchableAudioSource? AudioSource);

    /// <summary>Starts mirroring the desktop to an Apple TV (pairs first if needed).</summary>
    public async Task StartMirrorAsync(PickerEntry entry, CancellationToken ct)
    {
        if (_mirrors.ContainsKey(entry.Key)) return;
        var leader = entry.Leader;
        if (leader.Subtype is not AirPlayDeviceSubtype.AppleTv)
            throw new InvalidOperationException($"{leader.Name} does not support screen mirroring (Apple TV / AirPlay 2 TV only)");
        var address = leader.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"{leader.Name} has no IPv4 address yet");

        var credentials = _credentials.Load(leader.DeviceId);
        if (credentials is null)
        {
            if (PinPrompt is null) throw new PairingRequiredException(leader.Name);
            await PairLeaderAsync(entry, ct).ConfigureAwait(false);
            credentials = _credentials.Load(leader.DeviceId)
                ?? throw new InvalidOperationException("pairing did not produce credentials");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(ConnectTimeout);
        var session = await MirrorSession.ConnectAsync(address, leader.AirPlayPort ?? 7000, credentials,
            stage => SessionStage?.Invoke(entry.Key, stage), timeout.Token).ConfigureAwait(false);

        var settings = SettingsService.Instance.Current;
        var (adapterIdx, outputIdx) = SettingsService.Instance.GetDisplayIndices(settings.DisplaySourceId);

        var source = new ScreenMirrorSource(
            fps: settings.MaxFramerate,
            adapterIndex: adapterIdx,
            outputIndex: outputIdx,
            customWidth: settings.DownscaleWidth,
            customHeight: settings.DownscaleHeight);

        source.Diagnostic += d => SessionStage?.Invoke(entry.Key, $"capture: {d}");

        // Mirror carries audio in the same session (Apple TV syncs A/V itself). Mute the PC
        // and feed system audio (unless Dual Audio enabled), using the selected audio source.
        SwitchableAudioSource? mirrorAudio = null;
        if (session.HasAudio && !_audio.ContainsKey(entry.Key))
        {
            _mover.EnterStreaming(settings.AudioSourceId, settings.DualAudio);
            mirrorAudio = new SwitchableAudioSource(_mover.CreateCaptureSource(settings.AudioSourceId));
        }

        if (!_mirrors.TryAdd(entry.Key, new MirrorDestination(session, source, mirrorAudio)))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            await source.DisposeAsync().ConfigureAwait(false);
            mirrorAudio?.Dispose();
            return;
        }

        _ = session.StartStreamingAsync(source, mirrorAudio);
    }

    public async Task StopMirrorAsync(string key)
    {
        if (_mirrors.TryRemove(key, out var m))
        {
            await m.Session.DisposeAsync().ConfigureAwait(false);
            await m.Source.DisposeAsync().ConfigureAwait(false);
            m.AudioSource?.Dispose();
        }
        // Restore the local speakers once nothing (audio or mirror) is streaming.
        if (_audio.IsEmpty && _mirrors.IsEmpty) _mover.ExitStreaming();
    }

    // ------------------------------------------------------------ shared helpers

    private IReadOnlyList<GroupSession.Member> ResolveMembers(PickerEntry entry)
    {
        var members = GroupSession.MembersOf(entry, _credentials);
        if (members.Count == 0)
            throw new InvalidOperationException($"{entry.DisplayName} has no member with an IPv4 address yet");
        return members;
    }

    private async Task<GroupSession> ConnectAsync(PickerEntry entry,
        IReadOnlyList<GroupSession.Member> members, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        return await GroupSession.ConnectAsync(members,
            (memberName, stage) => SessionStage?.Invoke(entry.Key,
                members.Count > 1 ? $"{memberName}: {stage}" : stage),
            timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Runs the on-screen-PIN pairing flow against the entry's leader (Apple TV) and stores the result.</summary>
    private async Task PairLeaderAsync(PickerEntry entry, CancellationToken ct)
    {
        var leader = entry.Leader;
        var address = leader.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"{leader.Name} has no IPv4 address");

        SessionStage?.Invoke(entry.Key, $"{leader.Name} is showing a PIN — waiting for input");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PinEntryTimeout);

        using var pairing = await ReceiverPairing.BeginAsync(address, leader.AirPlayPort ?? 7000, timeout.Token)
            .ConfigureAwait(false);
        string? pin = await PinPrompt!(leader.Name).WaitAsync(timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pin))
            throw new OperationCanceledException("pairing cancelled — no PIN entered");

        var credentials = await pairing.FinishAsync(pin.Trim(), timeout.Token).ConfigureAwait(false);
        _credentials.Save(leader.DeviceId, credentials);
        SessionStage?.Invoke(entry.Key, $"paired with {leader.Name} — credentials stored");
    }

    public async ValueTask DisposeAsync()
    {
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        _lifetime.Cancel();
        foreach (string key in _audio.Keys.ToArray())
            await StopAudioAsync(key).ConfigureAwait(false);
        foreach (string key in _mirrors.Keys.ToArray())
            await StopMirrorAsync(key).ConfigureAwait(false);
        Task pendingAudioSourceSwitch;
        lock (_audioSourceSwitchLock)
            pendingAudioSourceSwitch = _audioSourceSwitchTask;
        await pendingAudioSourceSwitch.ConfigureAwait(false);
        _mover.Dispose();
        _lifetime.Dispose();
    }
}
