// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using WinPlay.App.Services;

namespace WinPlay.App.Views;

public sealed partial class SettingsPopupView : UserControl
{
    private double _aspectRatio = 16.0 / 9.0;
    private bool _suppressResolutionEvents;
    private bool _isPopulating;

    public event Action? Dismissed;

    public SettingsPopupView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateFromSettings();
    }

    public void Refresh()
    {
        PopulateFromSettings();
    }

    private void PopulateFromSettings()
    {
        _isPopulating = true;
        try
        {
            var settings = SettingsService.Instance.Current;

            // 1. Dual Audio
            DualAudioToggle.IsOn = settings.DualAudio;

            // 2. Audio Devices
            var audioDevices = SettingsService.Instance.GetAudioDevices();
            AudioSourceCombo.ItemsSource = audioDevices;

            AudioDeviceInfo? selectedAudio = null;
            if (!string.IsNullOrEmpty(settings.AudioSourceId))
            {
                selectedAudio = audioDevices.FirstOrDefault(a => string.Equals(a.Id, settings.AudioSourceId, StringComparison.OrdinalIgnoreCase));
            }
            AudioSourceCombo.SelectedItem = selectedAudio ?? audioDevices.FirstOrDefault();

            // 3. Display Devices
            var displayDevices = SettingsService.Instance.GetDisplayDevices();
            DisplaySourceCombo.ItemsSource = displayDevices;

            DisplayDeviceInfo? selectedDisplay = null;
            if (!string.IsNullOrEmpty(settings.DisplaySourceId))
            {
                selectedDisplay = displayDevices.FirstOrDefault(d => string.Equals(d.Id, settings.DisplaySourceId, StringComparison.OrdinalIgnoreCase));
            }
            selectedDisplay ??= displayDevices.FirstOrDefault(d => d.IsPrimary) ?? displayDevices.FirstOrDefault();
            DisplaySourceCombo.SelectedItem = selectedDisplay;

            // 4. Resolution & Aspect Ratio
            int dispW = selectedDisplay?.Width ?? 1920;
            int dispH = selectedDisplay?.Height ?? 1080;
            _aspectRatio = (double)dispW / dispH;

            _suppressResolutionEvents = true;
            DownscaleWidthBox.Value = settings.DownscaleWidth ?? dispW;
            DownscaleHeightBox.Value = settings.DownscaleHeight ?? dispH;
            _suppressResolutionEvents = false;

            AspectLockToggle.IsChecked = settings.AspectRatioLocked;
            AspectLockIcon.Glyph = settings.AspectRatioLocked ? "\uE72E" : "\uE785";
            UpdateAspectRatioText(dispW, dispH);

            // 5. Max Framerate
            int clampedFps = SettingsService.ClampFramerate(settings.MaxFramerate);
            MaxFramerateBox.Value = clampedFps;
        }
        finally
        {
            _isPopulating = false;
        }
    }

    private void UpdateAspectRatioText(int nativeW, int nativeH)
    {
        int gcd = GCD(nativeW, nativeH);
        string ratioStr = $"{nativeW / gcd}:{nativeH / gcd}";
        AspectRatioText.Text = $"Native: {nativeW}×{nativeH} (Aspect: {ratioStr})";
    }

    private static int GCD(int a, int b)
    {
        while (b != 0)
        {
            int t = b;
            b = a % b;
            a = t;
        }
        return a > 0 ? a : 1;
    }

    // ------------------------------------------------------------ event handlers

    private void DualAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isPopulating) return;
        var settings = SettingsService.Instance.Current;
        settings.DualAudio = DualAudioToggle.IsOn;
        SettingsService.Instance.Save(settings);
    }

    private void AudioSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating) return;
        if (AudioSourceCombo.SelectedItem is AudioDeviceInfo dev)
        {
            var settings = SettingsService.Instance.Current;
            settings.AudioSourceId = dev.Id;
            SettingsService.Instance.Save(settings);
        }
    }

    private void DisplaySourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating) return;
        if (DisplaySourceCombo.SelectedItem is DisplayDeviceInfo disp)
        {
            var settings = SettingsService.Instance.Current;
            settings.DisplaySourceId = disp.Id;

            // When switching display source, update resolution values, aspect ratio, etc.
            _aspectRatio = (double)disp.Width / disp.Height;
            _suppressResolutionEvents = true;
            DownscaleWidthBox.Value = disp.Width;
            DownscaleHeightBox.Value = disp.Height;
            _suppressResolutionEvents = false;

            settings.DownscaleWidth = disp.Width;
            settings.DownscaleHeight = disp.Height;

            UpdateAspectRatioText(disp.Width, disp.Height);
            SettingsService.Instance.Save(settings);
        }
    }

    private void DownscaleWidthBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isPopulating || _suppressResolutionEvents) return;
        if (double.IsNaN(sender.Value)) return;

        int w = (int)Math.Round(sender.Value);
        w = Math.Max(2, w & ~1);

        var settings = SettingsService.Instance.Current;
        settings.DownscaleWidth = w;

        if (settings.AspectRatioLocked)
        {
            int h = SettingsService.CalculateAspectResolution(w, _aspectRatio, isWidth: true);
            _suppressResolutionEvents = true;
            DownscaleHeightBox.Value = h;
            _suppressResolutionEvents = false;
            settings.DownscaleHeight = h;
        }

        SettingsService.Instance.Save(settings);
    }

    private void DownscaleHeightBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isPopulating || _suppressResolutionEvents) return;
        if (double.IsNaN(sender.Value)) return;

        int h = (int)Math.Round(sender.Value);
        h = Math.Max(2, h & ~1);

        var settings = SettingsService.Instance.Current;
        settings.DownscaleHeight = h;

        if (settings.AspectRatioLocked)
        {
            int w = SettingsService.CalculateAspectResolution(h, _aspectRatio, isWidth: false);
            _suppressResolutionEvents = true;
            DownscaleWidthBox.Value = w;
            _suppressResolutionEvents = false;
            settings.DownscaleWidth = w;
        }

        SettingsService.Instance.Save(settings);
    }

    private void AspectLockToggle_Click(object sender, RoutedEventArgs e)
    {
        bool locked = AspectLockToggle.IsChecked == true;
        AspectLockIcon.Glyph = locked ? "\uE72E" : "\uE785";

        var settings = SettingsService.Instance.Current;
        settings.AspectRatioLocked = locked;

        if (locked && !double.IsNaN(DownscaleWidthBox.Value))
        {
            // Sync height to current width
            int w = (int)Math.Round(DownscaleWidthBox.Value);
            w = Math.Max(2, w & ~1);
            int h = SettingsService.CalculateAspectResolution(w, _aspectRatio, isWidth: true);
            _suppressResolutionEvents = true;
            DownscaleHeightBox.Value = h;
            _suppressResolutionEvents = false;
            settings.DownscaleHeight = h;
        }

        SettingsService.Instance.Save(settings);
    }

    private void MaxFramerateBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isPopulating) return;
        if (double.IsNaN(sender.Value)) return;

        int fps = (int)Math.Round(sender.Value);
        int clamped = SettingsService.ClampFramerate(fps);
        if (fps != clamped)
        {
            sender.Value = clamped;
            return;
        }

        var settings = SettingsService.Instance.Current;
        settings.MaxFramerate = clamped;
        SettingsService.Instance.Save(settings);
    }

    private void ResetToDefaults_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.ResetToDefaults();
        PopulateFromSettings();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void Backdrop_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Hide();
    }

    // ------------------------------------------------------------ animations

    public void Show()
    {
        PopulateFromSettings();
        Visibility = Visibility.Visible;

        var visual = ElementCompositionPreview.GetElementVisual(CardBorder);
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        var dur = TimeSpan.FromMilliseconds(200);

        visual.Opacity = 0f;
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = dur;
        visual.StartAnimation("Opacity", fade);

        visual.Offset = new Vector3(0f, 20f, 0f);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, 20f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, ease);
        slide.Duration = dur;
        visual.StartAnimation("Offset", slide);
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible) return;

        var visual = ElementCompositionPreview.GetElementVisual(CardBorder);
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var dur = TimeSpan.FromMilliseconds(120);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, ease);
        fade.Duration = dur;
        visual.StartAnimation("Opacity", fade);

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3(0f, 10f, 0f), ease);
        slide.Duration = dur;
        visual.StartAnimation("Offset", slide);

        batch.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Dismissed?.Invoke();
        };
        batch.End();
    }
}
