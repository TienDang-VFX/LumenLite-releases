using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LumenLite.Widgets;

/// <summary>
/// An icon button, a fader (thin track, upright knob) and the percentage. Used for the
/// output volume, the microphone, each app in the mixer and monitor brightness.
/// </summary>
public sealed class VolumeBar : Grid
{
    public const string VolumeGlyph = "";
    public const string MutedGlyph = "";
    public const string MicGlyph = "";
    public const string MicOffGlyph = "";
    public const string BrightnessGlyph = "";

    private readonly Func<(float Level, bool Muted)?> _read;
    private readonly Action<float> _setLevel;
    private readonly Action<bool>? _setMute;
    private readonly string _glyph, _mutedGlyph;
    private readonly TextBlock? _icon;
    private readonly UIElement? _customIcon;
    private readonly Grid _track = new() { Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 10, 0) };
    private readonly Border _fill = new() { Height = 2, Background = Material.Display.Accent, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _knob = new() { Width = 8, Height = 16, CornerRadius = new CornerRadius(2), Background = Material.Display.Text, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _percent = new() { FontFamily = Theme.Mono, FontSize = 11, Foreground = Material.Display.Secondary, Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private float _level;
    private bool _muted;

    /// <summary>The default output's master volume.</summary>
    public VolumeBar() : this(Audio.Volume, Audio.SetVolume, Audio.SetMute) { }

    /// <param name="icon">Replaces the glyph, e.g. an app icon; clicking it still mutes.</param>
    public VolumeBar(Func<(float Level, bool Muted)?> read, Action<float> setLevel, Action<bool>? setMute,
        string glyph = VolumeGlyph, string mutedGlyph = MutedGlyph, UIElement? icon = null, string? tooltip = null)
    {
        _read = read;
        _setLevel = setLevel;
        _setMute = setMute;
        _glyph = glyph;
        _mutedGlyph = mutedGlyph;
        _customIcon = icon;

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Margin = new Thickness(0, 0, 0, 2);

        UIElement content = icon ?? (_icon = Material.Display.Glyph(glyph, 15));
        if (content is FrameworkElement element)
        {
            element.Width = 18;
            element.Height = 18;
        }
        FrameworkElement button = setMute != null
            ? Material.Display.Pressable(content, ToggleMute, new Thickness(6))
            : new Border { Child = content, Padding = new Thickness(6) };
        button.Margin = new Thickness(-6, 0, 0, 0);
        button.ToolTip = tooltip ?? (setMute != null ? L.T("Tắt/bật tiếng") : null);
        Children.Add(button);

        _track.Children.Add(new Border { Height = 2, Background = Material.Display.Track });
        _track.Children.Add(_fill);
        _track.Children.Add(_knob);
        SetColumn(_track, 1);
        Children.Add(_track);

        SetColumn(_percent, 2);
        Children.Add(_percent);

        _track.SizeChanged += (_, _) => Draw();
        _track.MouseLeftButtonDown += (_, e) =>
        {
            _track.CaptureMouse();
            SetFromMouse(e);
            e.Handled = true;
        };
        _track.MouseMove += (_, e) =>
        {
            if (_track.IsMouseCaptured)
                SetFromMouse(e);
        };
        _track.MouseLeftButtonUp += (_, e) =>
        {
            _track.ReleaseMouseCapture();
            e.Handled = true;
        };
        _track.MouseWheel += (_, e) =>
        {
            Apply(_level + Math.Sign(e.Delta) * 0.02f);
            e.Handled = true;
        };
    }

    public bool IsDragging => _track.IsMouseCaptured;

    /// <summary>Re-reads the level, unless the user is dragging the bar.</summary>
    public void Refresh()
    {
        if (IsDragging)
            return;
        try
        {
            Show(_read());
        }
        catch (Exception ex)
        {
            Store.Log("level read failed: " + ex.Message);
        }
    }

    /// <summary>Shows a level read elsewhere (e.g. on a background thread).</summary>
    public void Show((float Level, bool Muted)? value)
    {
        if (IsDragging)
            return;
        IsEnabled = value != null;
        if (value is { } v)
        {
            _level = v.Level;
            _muted = v.Muted;
        }
        Draw();
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        var usable = Math.Max(1, _track.ActualWidth - _knob.Width);
        Apply((float)((e.GetPosition(_track).X - _knob.Width / 2) / usable));
    }

    private void Apply(float level)
    {
        _level = Math.Clamp(level, 0f, 1f);
        if (_level > 0 && _setMute != null)
            _muted = false;
        try
        {
            _setLevel(_level);
        }
        catch (Exception ex)
        {
            Store.Log("level set failed: " + ex.Message);
        }
        Draw();
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        try
        {
            _setMute?.Invoke(_muted);
        }
        catch (Exception ex)
        {
            Store.Log("mute failed: " + ex.Message);
        }
        Draw();
    }

    private void Draw()
    {
        var usable = Math.Max(0, _track.ActualWidth - _knob.Width);
        var shown = _muted ? 0 : _level;
        _fill.Width = usable * shown + _knob.Width / 2;
        _knob.Margin = new Thickness(usable * shown, 0, 0, 0);
        _fill.Opacity = _muted ? 0.4 : 1;
        if (_icon != null)
            _icon.Text = _muted || (_level == 0 && _setMute != null) ? _mutedGlyph : _glyph;
        if (_customIcon != null)
            _customIcon.Opacity = _muted ? 0.35 : 1;
        _percent.Text = _muted ? L.T("Tắt") : $"{Math.Round(_level * 100)}%";
    }
}
