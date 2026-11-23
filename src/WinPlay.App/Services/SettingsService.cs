// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Vortice.DXGI;
using WinPlay.Core.Settings;

namespace WinPlay.App.Services;

public sealed record AudioDeviceInfo(
    string Id,
    string DisplayName,
    bool IsDefault);

public sealed record DisplayDeviceInfo(
    string Id,
    string DisplayName,
    int AdapterIndex,
    int OutputIndex,
    int Width,
    int Height,
    bool IsPrimary);

/// <summary>
/// Manages loading, saving, resetting, and applying user settings and hardware device discovery.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<SettingsService> _lazyInstance = new(() => new SettingsService());
    public static SettingsService Instance => _lazyInstance.Value;

    private readonly string _settingsFilePath;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string? customPath = null)
    {
        _settingsFilePath = customPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinPlay", "settings.json");

        Current = Load();
    }

    /// <summary>Loads settings from file or creates defaults if missing or corrupted.</summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        settings.MaxFramerate = ClampFramerate(settings.MaxFramerate);
                        if (settings.DownscaleWidth.HasValue)
                            settings.DownscaleWidth = Math.Max(2, settings.DownscaleWidth.Value & ~1);
                        if (settings.DownscaleHeight.HasValue)
                            settings.DownscaleHeight = Math.Max(2, settings.DownscaleHeight.Value & ~1);
                        Current = settings;
                        return settings;
                    }
                }
                catch (Exception)
                {
                    // Fall back to defaults on read error
                }
            }

            var defaults = CreateDefaultSettings();
            Current = defaults;
            return defaults;
        }
    }

    /// <summary>Saves current settings to disk and fires the SettingsChanged event.</summary>
    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            settings.MaxFramerate = ClampFramerate(settings.MaxFramerate);
            if (settings.DownscaleWidth.HasValue)
                settings.DownscaleWidth = Math.Max(2, settings.DownscaleWidth.Value & ~1);
            if (settings.DownscaleHeight.HasValue)
                settings.DownscaleHeight = Math.Max(2, settings.DownscaleHeight.Value & ~1);

            Current = settings.Clone();
            try
            {
                string dir = Path.GetDirectoryName(_settingsFilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception) { /* persistence failure shouldn't crash app */ }
        }

        SettingsChanged?.Invoke(Current);
    }

    /// <summary>Resets settings to system defaults, saves to disk, and notifies listeners.</summary>
    public AppSettings ResetToDefaults()
    {
        var defaults = CreateDefaultSettings();
        Save(defaults);
        return defaults;
    }

    public AppSettings CreateDefaultSettings()
    {
        var displays = GetDisplayDevices();
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        return new AppSettings
        {
            DualAudio = false,
            AudioSourceId = "", // default system endpoint
            DisplaySourceId = primary?.Id ?? "",
            DownscaleWidth = primary?.Width,
            DownscaleHeight = primary?.Height,
            AspectRatioLocked = true,
            MaxFramerate = 60,
        };
    }

    public static int ClampFramerate(int fps) => SettingsCalculations.ClampFramerate(fps);

    public static int CalculateAspectResolution(int value, double aspectRatio, bool isWidth) =>
        SettingsCalculations.CalculateAspectResolution(value, aspectRatio, isWidth);

    /// <summary>Enumerates active audio output endpoints on this PC.</summary>
    public List<AudioDeviceInfo> GetAudioDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = defaultDev?.ID;
            }
            catch { }

            // Prepend Default choice
            list.Add(new AudioDeviceInfo("", "Default (System Output)", defaultId == null));

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var dev in devices)
            {
                using (dev)
                {
                    bool isDef = defaultId != null && string.Equals(dev.ID, defaultId, StringComparison.OrdinalIgnoreCase);
                    string name = isDef ? $"{dev.FriendlyName} (Default)" : dev.FriendlyName;
                    list.Add(new AudioDeviceInfo(dev.ID, name, isDef));
                }
            }
        }
        catch (Exception)
        {
            if (list.Count == 0)
                list.Add(new AudioDeviceInfo("", "Default (System Output)", true));
        }
        return list;
    }

    /// <summary>Enumerates connected displays across all GPU adapters via DXGI and user32.</summary>
    public List<DisplayDeviceInfo> GetDisplayDevices()
    {
        var list = new List<DisplayDeviceInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory != null)
            {
                for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                {
                    using (adapter)
                    {
                        for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        {
                            using (output)
                            {
                                var desc = output.Description;
                                int w = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                                int h = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
                                bool isPrimary = desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0;

                                string monitorName = GetMonitorFriendlyName(desc.DeviceName);
                                if (string.IsNullOrWhiteSpace(monitorName))
                                    monitorName = $"Display {list.Count + 1}";

                                string displayName = isPrimary
                                    ? $"{monitorName} ({w}×{h}) - Primary"
                                    : $"{monitorName} ({w}×{h})";

                                list.Add(new DisplayDeviceInfo(
                                    desc.DeviceName,
                                    displayName,
                                    (int)a,
                                    (int)o,
                                    w,
                                    h,
                                    isPrimary));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception) { }

        if (list.Count == 0)
        {
            list.Add(new DisplayDeviceInfo(@"\\.\DISPLAY1", "Display 1 (1920×1080) - Primary", 0, 0, 1920, 1080, true));
        }
        return list;
    }

    public (int adapterIndex, int outputIndex) GetDisplayIndices(string? displaySourceId)
    {
        var displays = GetDisplayDevices();
        if (!string.IsNullOrEmpty(displaySourceId))
        {
            var matched = displays.FirstOrDefault(d => string.Equals(d.Id, displaySourceId, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return (matched.AdapterIndex, matched.OutputIndex);
        }

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        return (primary?.AdapterIndex ?? 0, primary?.OutputIndex ?? 0);
    }

    public (int width, int height) GetDisplayResolution(string? displaySourceId)
    {
        var displays = GetDisplayDevices();
        if (!string.IsNullOrEmpty(displaySourceId))
        {
            var matched = displays.FirstOrDefault(d => string.Equals(d.Id, displaySourceId, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
                return (matched.Width, matched.Height);
        }

        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        return (primary?.Width ?? 1920, primary?.Height ?? 1080);
    }

    private static string GetMonitorFriendlyName(string deviceName)
    {
        try
        {
            var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            // First call with deviceName gives the attached monitor device
            if (EnumDisplayDevices(deviceName, 0, ref dev, 0))
            {
                if (!string.IsNullOrWhiteSpace(dev.DeviceString))
                    return dev.DeviceString;
            }
        }
        catch { }
        return "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
