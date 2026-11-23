// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using WinPlay.Core.Mirror;

namespace WinPlay.Capture;

/// <summary>
/// <see cref="IH264VideoSource"/> that mirrors the primary desktop: DXGI Desktop
/// Duplication → GPU BGRA → NV12 (color-convert + scale) → Media Foundation H.264 →
/// Annex-B access units. The encode resolution is negotiated from the receiver's
/// advertised display size (never hardcoded), fit to the desktop aspect ratio. Runs the
/// capture/encode on a dedicated MTA thread paced to the target frame rate; when the
/// screen is unchanged it re-encodes the previous frame so the receiver's clock keeps
/// advancing (which the AirPlay pipeline requires).
/// </summary>
public sealed class ScreenMirrorSource : IH264VideoSource
{
    private readonly int _fps;
    private readonly int _bitrateOverride;
    private readonly object _configLock = new();
    private int _adapterIndex;
    private int _outputIndex;
    private readonly int? _customWidth;
    private readonly int? _customHeight;
    private volatile bool _configChanged;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _receiverDisplayW;
    private int _receiverDisplayH;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public event Action<ReadOnlyMemory<byte>, bool>? FrameEncoded;
    public event Action<string>? Diagnostic;

    /// <summary>Dynamically updates display output while streaming.</summary>
    public void UpdateConfiguration(int adapterIndex, int outputIndex)
    {
        lock (_configLock)
        {
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
            _configChanged = true;
        }
    }

    /// <param name="fps">Max framerate in fps (clamped between 24 and 60).</param>
    /// <param name="bitrateMbps">Encoder bitrate; 0 = auto (scaled to the negotiated resolution).</param>
    /// <param name="adapterIndex">DXGI adapter index.</param>
    /// <param name="outputIndex">DXGI output (display) index.</param>
    /// <param name="customWidth">Optional downscaled width in pixels.</param>
    /// <param name="customHeight">Optional downscaled height in pixels.</param>
    public ScreenMirrorSource(
        int fps = 60,
        int bitrateMbps = 0,
        int adapterIndex = 0,
        int outputIndex = 0,
        int? customWidth = null,
        int? customHeight = null)
    {
        _fps = Math.Clamp(fps, 24, 60);
        _bitrateOverride = bitrateMbps * 1_000_000;
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        _customWidth = customWidth;
        _customHeight = customHeight;
    }

    public void Configure(int receiverDisplayWidth, int receiverDisplayHeight)
    {
        _receiverDisplayW = receiverDisplayWidth;
        _receiverDisplayH = receiverDisplayHeight;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(() => CaptureLoop(ct))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "WinPlay-Mirror",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public void RequestKeyframe() { /* the MFT emits an IDR + SPS/PPS as its first output automatically */ }

    private void CaptureLoop(CancellationToken ct)
    {
        DesktopDuplicator? capture = null;
        GpuColorConverter? converter = null;
        IH264Encoder? encoder = null;
        try
        {
            capture = new DesktopDuplicator(_adapterIndex, _outputIndex);
            int srcW = capture.Width, srcH = capture.Height;

            int targetW = srcW;
            int targetH = srcH;
            if (_customWidth.HasValue && _customHeight.HasValue && _customWidth.Value > 0 && _customHeight.Value > 0)
            {
                targetW = Math.Min(srcW, _customWidth.Value & ~1);
                targetH = Math.Min(srcH, _customHeight.Value & ~1);
            }

            // Negotiate the encode size: fit the desktop into the receiver's advertised
            // display (or use the desktop size if none advertised). Even dimensions only.
            (Width, Height) = NegotiateEncodeSize(targetW, targetH, _receiverDisplayW, _receiverDisplayH);
            int bitrate = _bitrateOverride > 0 ? _bitrateOverride : AutoBitrate(Width, Height, _fps);

            // GPU color-convert + scale (BGRA → NV12) on the capture device — the heaviest
            // per-frame operation runs on the GPU (any vendor), keeping latency low.
            converter = new GpuColorConverter(capture.Device, srcW, srcH, Width, Height, _fps);

            // Prefer the GPU hardware encoder (NVENC/QuickSync/AMF/Adreno); fall back to
            // the software MFT only if no hardware encoder is present.
            encoder = CreateEncoder(Width, Height, _fps, bitrate);
            encoder.Encoded += au => FrameEncoded?.Invoke(au, IsKeyframe(au));
            Diagnostic?.Invoke($"capture {srcW}x{srcH} → encode {Width}x{Height} @ {_fps}fps, {bitrate / 1_000_000} Mbps ({encoder.Name})");

            byte[] nv12 = new byte[Width * Height * 3 / 2]; // NV12 = Y (w*h) + interleaved UV (w*h/2)
            bool haveFrame = false;

            var sw = Stopwatch.StartNew();
            long frameIndex = 0;

            while (!_stopped && !ct.IsCancellationRequested)
            {
                if (_configChanged)
                {
                    _configChanged = false;
                    int curAdapter, curOutput;
                    lock (_configLock)
                    {
                        curAdapter = _adapterIndex;
                        curOutput = _outputIndex;
                    }
                    try
                    {
                        encoder?.Dispose();
                        converter?.Dispose();
                        capture?.Dispose();

                        capture = new DesktopDuplicator(curAdapter, curOutput);
                        srcW = capture.Width;
                        srcH = capture.Height;

                        targetW = srcW;
                        targetH = srcH;
                        if (_customWidth.HasValue && _customHeight.HasValue && _customWidth.Value > 0 && _customHeight.Value > 0)
                        {
                            targetW = Math.Min(srcW, _customWidth.Value & ~1);
                            targetH = Math.Min(srcH, _customHeight.Value & ~1);
                        }
                        (Width, Height) = NegotiateEncodeSize(targetW, targetH, _receiverDisplayW, _receiverDisplayH);
                        bitrate = _bitrateOverride > 0 ? _bitrateOverride : AutoBitrate(Width, Height, _fps);

                        converter = new GpuColorConverter(capture.Device, srcW, srcH, Width, Height, _fps);
                        encoder = CreateEncoder(Width, Height, _fps, bitrate);
                        encoder.Encoded += au => FrameEncoded?.Invoke(au, IsKeyframe(au));
                        nv12 = new byte[Width * Height * 3 / 2];
                        haveFrame = false;
                        Diagnostic?.Invoke($"switched to display: {srcW}x{srcH} → encode {Width}x{Height} @ {_fps}fps ({encoder.Name})");
                    }
                    catch (Exception ex)
                    {
                        Diagnostic?.Invoke($"reconfiguration error: {ex.Message}");
                    }
                }

                double frameMs = 1000.0 / _fps;
                double dueMs = frameIndex * frameMs;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    Thread.Sleep(Math.Max(1, (int)(dueMs - nowMs)));
                    continue;
                }

                if (capture.TryCaptureInto(converter.InputTexture, timeoutMs: 12))
                {
                    converter.Convert(nv12);
                    haveFrame = true;
                }
                if (!haveFrame) { frameIndex++; continue; } // nothing captured yet

                encoder.Encode(nv12);
                frameIndex++;
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Diagnostic?.Invoke($"capture/encode error: {ex.Message}");
        }
        finally
        {
            encoder?.Dispose();
            converter?.Dispose();
            capture?.Dispose();
        }
    }

    private IH264Encoder CreateEncoder(int width, int height, int fps, int bitrate)
    {
        try
        {
            return new HardwareH264Encoder(width, height, fps, bitrate);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"hardware encoder unavailable ({ex.Message}); using software");
            return new MediaFoundationH264Encoder(width, height, fps, bitrate);
        }
    }

    private static bool IsKeyframe(byte[] annexB) => H264.ContainsKeyframe(H264.SplitAnnexB(annexB));

    /// <summary>
    /// Fits the desktop into the receiver's display (if advertised), preserving aspect
    /// ratio; both dimensions rounded down to even (NV12 requires even width and height).
    /// No fixed cap — the resolution comes from what the receiver reports it can present.
    /// </summary>
    internal static (int Width, int Height) NegotiateEncodeSize(int srcW, int srcH, int dispW, int dispH)
    {
        int w = srcW, h = srcH;
        if (dispW > 0 && dispH > 0 && (srcW > dispW || srcH > dispH))
        {
            double scale = Math.Min((double)dispW / srcW, (double)dispH / srcH);
            w = (int)Math.Round(srcW * scale);
            h = (int)Math.Round(srcH * scale);
        }
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }

    /// <summary>Bitrate scaled to resolution + frame rate (~0.10 bits/pixel/frame), clamped to a sane range.</summary>
    internal static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * height * fps * 0.10);
        return (int)Math.Clamp(bits, 4_000_000, 60_000_000);
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;
        _thread?.Join(2000);
        return ValueTask.CompletedTask;
    }
}
