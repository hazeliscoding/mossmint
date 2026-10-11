using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Mossmint.Desktop.Services;

// The user's own colours from a Windows contrast theme (Aquatic, Desert, Dusk, Night sky).
public sealed record ContrastPalette(
    Color Window, Color WindowText, Color Highlight, Color HighlightText, Color HotLight, Color GrayText);

// While a contrast theme is on, every Mossmint colour and the Fluent controls' colours
// come from it instead of the moss palette (EN 301 549 §11.7). Status keeps its shape and
// label, so nothing depends on the hues that are dropped. The mapping is in
// docs/design/design-system.md.
public static class ContrastTheme
{
    private static ResourceDictionary? _applied;

    public static void Follow(Avalonia.Application app)
    {
        if (app.PlatformSettings is not { } settings)
        {
            return;
        }

        void Update(PlatformColorValues values) =>
            Apply(app, values.ContrastPreference == ColorContrastPreference.High ? SystemPalette() : null);

        Update(settings.GetColorValues());
        settings.ColorValuesChanged += (_, values) => Update(values);
    }

    // null turns it off and hands the colours back to the light or dark theme.
    public static void Apply(Avalonia.Application app, ContrastPalette? palette)
    {
        if (_applied is not null)
        {
            app.Resources.MergedDictionaries.Remove(_applied);
            _applied = null;
        }

        if (palette is null)
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            return;
        }

        _applied = Resources(palette);
        app.Resources.MergedDictionaries.Add(_applied);

        // Fluent only has light and dark bases; pick the one nearer the user's background.
        app.RequestedThemeVariant = Luminance(palette.Window) < 0.5 ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static ResourceDictionary Resources(ContrastPalette p)
    {
        var (back, text, mark, onMark) = (p.Window, p.WindowText, p.Highlight, p.HighlightText);
        // Pairs, not a dictionary: a theme may use one colour for two roles.
        (Color, string[])[] brushes =
        [
            (back,
            [
                "BgCanvas", "BgPanel", "BgSurface", "BgElevated", "BgInset", "AccentSubtle",
                "SignalHealthySubtle", "SignalWarningSubtle", "SignalCriticalSubtle", "SignalInfoSubtle", "SignalNeutralSubtle",
            ]),
            (text,
            [
                "TextPrimary", "TextSecondary", "TextFaint", "AccentText", "BorderHairline", "BorderDefault", "BorderStrong",
                "SignalHealthy", "SignalHealthyText", "SignalHealthyBorder", "SignalWarning", "SignalWarningText", "SignalWarningBorder",
                "SignalCritical", "SignalCriticalText", "SignalCriticalBorder", "SignalInfo", "SignalInfoText", "SignalInfoBorder",
                "SignalNeutral", "SignalNeutralText", "SignalNeutralBorder", "Loss", "LossText",
            ]),
            (mark, ["Accent", "AccentHover", "AccentActive", "AccentBorder", "BgSelected"]),
            (onMark, ["TextInverse", "TextSelected"]),
            (p.HotLight, ["LinkText"]),
            (p.GrayText, ["TextDisabled"]),
        ];
        (Color, string[])[] colours =
        [
            (back,
            [
                "SystemAltHighColor", "SystemAltLowColor", "SystemAltMediumColor", "SystemAltMediumHighColor", "SystemAltMediumLowColor",
                "SystemChromeLowColor", "SystemChromeMediumColor", "SystemChromeMediumLowColor", "SystemChromeHighColor", "SystemChromeAltLowColor",
                "SystemChromeBlackHighColor", "SystemChromeBlackLowColor", "SystemChromeBlackMediumColor", "SystemChromeBlackMediumLowColor",
                "SystemRegionColor", "SystemListLowColor", "SystemListMediumColor",
            ]),
            (text,
            [
                "SystemBaseHighColor", "SystemBaseMediumHighColor", "SystemBaseMediumColor", "SystemBaseMediumLowColor", "SystemBaseLowColor",
                "SystemChromeWhiteColor", "SystemErrorTextColor",
            ]),
            (mark,
            [
                "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
                "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            ]),
            (p.GrayText, ["SystemChromeDisabledHighColor", "SystemChromeDisabledLowColor", "SystemChromeGrayColor"]),
        ];

        var resources = new ResourceDictionary();
        foreach (var (colour, keys) in brushes)
        {
            foreach (var key in keys)
            {
                resources[key] = new SolidColorBrush(colour);
            }
        }

        foreach (var (colour, keys) in colours)
        {
            foreach (var key in keys)
            {
                resources[key] = colour;
            }
        }

        return resources;
    }

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255;

    // A local call into Windows; the system holds the theme's colours. Elsewhere there's
    // nothing to read and the light or dark theme stays.
    private static ContrastPalette? SystemPalette()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // COLOR_WINDOW, _WINDOWTEXT, _HIGHLIGHT, _HIGHLIGHTTEXT, _HOTLIGHT and _GRAYTEXT; COLORREF is 0x00BBGGRR.
        static Color Sys(int index)
        {
            var c = GetSysColor(index);
            return Color.FromRgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }

        return new(Sys(5), Sys(8), Sys(13), Sys(14), Sys(26), Sys(17));
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
