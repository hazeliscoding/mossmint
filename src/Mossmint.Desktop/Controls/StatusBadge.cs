using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Mossmint.Desktop.Controls;

public enum StatusTone
{
    Neutral,
    Healthy,
    Warning,
    Critical,
    Info,
    Accent,
}

// A status always pairs a shape with a text label (see Themes/Controls.axaml),
// so it reads the same without colour and to a screen reader.
public sealed class StatusBadge : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string?>(nameof(Text));

    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusBadge, StatusTone>(nameof(Tone));

    public StatusBadge()
    {
        UpdateTone(Tone);

        // The label inside is part of the template, which UI Automation leaves out, so the
        // badge itself is the text Narrator reads.
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Text);
        AutomationProperties.SetIsControlElementOverride(this, true);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty)
        {
            UpdateTone(change.GetNewValue<StatusTone>());
        }
        else if (change.Property == TextProperty)
        {
            AutomationProperties.SetName(this, change.GetNewValue<string?>());
        }
    }

    private void UpdateTone(StatusTone tone)
    {
        PseudoClasses.Set(":healthy", tone == StatusTone.Healthy);
        PseudoClasses.Set(":warning", tone == StatusTone.Warning);
        PseudoClasses.Set(":critical", tone == StatusTone.Critical);
        PseudoClasses.Set(":info", tone == StatusTone.Info);
        PseudoClasses.Set(":accent", tone == StatusTone.Accent);
    }
}
