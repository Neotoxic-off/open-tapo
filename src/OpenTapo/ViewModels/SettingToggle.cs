using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenTapo.ViewModels;

/// <summary>A labelled on/off device setting; hidden when the device doesn't report it.</summary>
public sealed partial class SettingToggle(string label, string glyph, Func<bool, Task> apply) : ObservableObject
{
    private bool _isLoading;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private bool _isSupported;

    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    /// <summary>Shows the device's value without sending it back.</summary>
    public void Load(bool? value)
    {
        _isLoading = true;
        try
        {
            IsSupported = value.HasValue;
            IsOn = value ?? false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_isLoading)
        {
            _ = apply(value);
        }
    }
}
