using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Services;
using Mossmint.Desktop.ViewModels;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.SmokeTests;

public partial class AccessibilityTests
{
    private static readonly string[] Surfaces = ["BgCanvas", "BgPanel", "BgSurface", "BgElevated"];

    // WCAG 2.2 1.4.3: text needs 4.5:1 on every surface it sits on. 1.4.11: the focus
    // ring and chart marks need 3:1. Badges put signal text on its tint over a surface.
    private static IEnumerable<(string Fore, string Back, string? Tint, double Needs)> Pairs() =>
    [
        .. new[] { "TextPrimary", "TextSecondary", "TextFaint", "AccentText", "LossText" }.SelectMany(text => Surfaces.Select(s => (text, s, (string?)null, 4.5))),
        .. new[] { "Healthy", "Warning", "Critical", "Info", "Neutral" }.SelectMany(signal => Surfaces.Select(s => ($"Signal{signal}Text", s, (string?)$"Signal{signal}Subtle", 4.5))),
        ("SignalHealthyText", "BgCanvas", null, 4.5),
        ("SignalCriticalText", "BgCanvas", null, 4.5),
        ("TextInverse", "Accent", null, 4.5),
        .. new[] { "Accent", "SignalInfo", "SignalWarning" }.SelectMany(mark => Surfaces.Select(s => (mark, s, (string?)null, 3.0))),
    ];

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Theme_colour_pairs_meet_their_contrast_ratios(string theme)
    {
        using var app = new Harness();

        Failures(Pairs(), Variant(theme)).Should().BeEmpty();
    }

    [AvaloniaFact]
    public void The_contrast_check_catches_a_weak_pair()
    {
        using var app = new Harness();

        // Positive control: disabled text is meant to be faint, and #777 on white is the textbook 4.48:1.
        Failures([("TextDisabled", "BgSurface", null, 4.5)], ThemeVariant.Light).Should().ContainSingle();
        Contrast(Color.Parse("#777777"), Colors.White).Should().BeApproximately(4.48, 0.01);
    }

    // Close to Windows 11's Night sky and Desert contrast themes: one dark, one light.
    private static readonly ContrastPalette NightSky = new(
        Color.Parse("#000000"), Color.Parse("#FFFFFF"), Color.Parse("#D6B4FD"), Color.Parse("#2B2B2B"), Color.Parse("#8080FF"), Color.Parse("#A6A6A6"));

    private static readonly ContrastPalette Desert = new(
        Color.Parse("#FFFAEF"), Color.Parse("#3D3D3D"), Color.Parse("#903909"), Color.Parse("#FFF5E3"), Color.Parse("#1C5E75"), Color.Parse("#676767"));

    [AvaloniaTheory]
    [InlineData("Night sky")]
    [InlineData("Desert")]
    public void A_contrast_theme_replaces_every_colour(string theme)
    {
        using var app = new Harness();
        var palette = theme == "Desert" ? Desert : NightSky;

        ContrastTheme.Apply(Avalonia.Application.Current!, palette);
        try
        {
            Leaks(palette).Should().BeEmpty();
        }
        finally
        {
            ContrastTheme.Apply(Avalonia.Application.Current!, null);
        }
    }

    [AvaloniaFact]
    public void The_contrast_check_sees_the_moss_colours()
    {
        using var app = new Harness();

        // Positive control: with no contrast theme on, the tokens hold Mossmint's own colours.
        Leaks(NightSky).Should().Contain(s => s.StartsWith("BgCanvas", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Every_focusable_control_has_a_name_narrator_can_read()
    {
        using var app = new Harness();

        string.Join(Environment.NewLine, await Tour(app, where => Unnamed(app.Window!, where))).Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Every_name_given_reaches_narrator()
    {
        using var app = new Harness();

        string.Join(Environment.NewLine, await Tour(app, where => Inert(app.Window!, where))).Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Each_page_has_a_heading_inside_a_main_landmark_beside_the_navigation()
    {
        using var app = await Unlocked();
        var missing = new List<string>();
        foreach (var destination in Enum.GetValues<Destination>())
        {
            app.ViewModel.OpenApp(destination);
            Harness.Pump();
            var title = app.Window!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("page-title") && t.IsEffectivelyVisible);
            var main = title.GetVisualAncestors().OfType<Control>().Select(ControlAutomationPeer.CreatePeerForElement).FirstOrDefault(p => p.GetLandmarkType() is not null);
            if (ControlAutomationPeer.CreatePeerForElement(title).GetHeadingLevel() != 1 || main?.GetLandmarkType() != AutomationLandmarkType.Main)
            {
                missing.Add(destination.ToString());
            }
        }

        var nav = app.Window!.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("nav"));
        (missing, ControlAutomationPeer.CreatePeerForElement(nav).GetLandmarkType()).Should().BeEquivalentTo((new List<string>(), (AutomationLandmarkType?)AutomationLandmarkType.Navigation));
    }

    [AvaloniaFact]
    public void The_inert_name_check_sees_a_name_narrator_skips()
    {
        using var app = new Harness();

        // Positive control: a plain Border isn't a control element, so its name is never read; a
        // text block reads its text, not its name; a masked text box still hands over its text.
        var window = new Window
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Border { [AutomationProperties.NameProperty] = "Card" },
                    new Border { [AutomationProperties.NameProperty] = "Group", [AutomationProperties.IsControlElementOverrideProperty] = true },
                    new TextBlock { Text = "1. Cash", [AutomationProperties.NameProperty] = "Cash, step 1 of 5" },
                    new TextBox { PasswordChar = '•', Text = "secret", [AutomationProperties.NameProperty] = "Passphrase" },
                    new Controls.PassphraseBox { Text = "secret", [AutomationProperties.NameProperty] = "Passphrase" },
                    new Controls.StatusBadge { Text = "Estimated" },
                },
            },
        };
        window.Show();

        Inert(window, "Sample").Should().HaveCount(3)
            .And.Contain(s => s.Contains("'Card'")).And.Contain(s => s.Contains("'Cash, step 1 of 5' reads as '1. Cash'")).And.Contain(s => s.Contains("TextBox 'Passphrase'"));
        window.Close();
    }

    // The smallest window fits 200% on a 1080p screen (about 960x490 above the taskbar)
    // and half of one at 125% (768 wide).
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_is_cut_off_in_a_default_or_smallest_window(bool smallest)
    {
        using var app = new Harness();

        string.Join(Environment.NewLine, await Tour(app, where => CutOff(app.Window!, where), smallest)).Should().BeEmpty();
    }

    [AvaloniaFact]
    public void The_cut_off_check_sees_what_cannot_be_reached()
    {
        using var app = new Harness();

        // Positive control: a button past the edge of a panel that can't scroll, a trimmed figure and one spilling over.
        // The same button in a ScrollViewer can be scrolled to.
        var window = new Window
        {
            Width = 300,
            Height = 200,
            Content = new StackPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 280 }, new Button { Content = "Hidden" } } },
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 280 }, new Button { Content = "Scrolls" } } },
                    },
                    new TextBlock { Classes = { "mono" }, Width = 30, TextTrimming = TextTrimming.CharacterEllipsis, Text = "$1,105.00" },
                    new TextBlock { Classes = { "mono" }, Width = 30, Text = "$2,050.00" },
                },
            },
        };
        window.Show();

        CutOff(window, "Sample").Should().HaveCount(3).And.Contain(s => s.Contains("Hidden")).And.Contain(s => s.Contains("$1,105.00")).And.Contain(s => s.Contains("$2,050.00"));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Keyboard_focus_draws_a_ring_around_the_control(string theme)
    {
        using var app = new Harness();

        RingPixels(Variant(theme), withRing: true).Should().BeGreaterThan(100);
    }

    [AvaloniaFact]
    public void The_ring_check_sees_a_missing_ring()
    {
        using var app = new Harness();

        // Positive control: the same button with no focus adorner draws nothing outside itself.
        RingPixels(ThemeVariant.Dark, withRing: false).Should().Be(0);
    }

    [AvaloniaFact]
    public async Task Focus_moves_into_a_form_and_back_to_what_opened_it()
    {
        using var app = await Unlocked();
        var shell = app.Screen<ShellViewModel>();

        shell.Navigate(Destination.Savings);
        var savings = (SavingsPageViewModel)shell.CurrentPage!;
        Press(app, "Add goal", savings.StartAddCommand);
        Focused(app).Should().Be("Goal name");
        Press(app, "Cancel", savings.CancelFormsCommand);
        Focused(app).Should().Be("Add goal");

        // Saving rebuilds the goal cards; focus finds the new card's button.
        Press(app, "Update saved for Car fund", savings.StartUpdateCommand, savings.Goals[0]);
        Focused(app).Should().Be("Saved so far");
        savings.SavedText = "1300";
        Press(app, "Save", savings.SaveSavedCommand);
        Focused(app).Should().Be("Update saved for Car fund");

        // Escape closes a form like Cancel does.
        Press(app, "Add goal", savings.StartAddCommand);
        app.Window!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Harness.Pump();
        (savings.IsAdding, Focused(app)).Should().Be((false, "Add goal"));

        shell.Navigate(Destination.Debts);
        var debts = (DebtsPageViewModel)shell.CurrentPage!;
        Press(app, "Confirm balance", debts.StartConfirmCommand);
        Focused(app).Should().Be("Balance owed");
        Press(app, "Cancel", debts.CancelConfirmCommand);
        Focused(app).Should().Be("Confirm balance");

        shell.Navigate(Destination.Bills);
        var bills = (BillsPageViewModel)shell.CurrentPage!;
        Press(app, "Mark Rent paid", bills.MarkPaidCommand, bills.Rows.Single(r => r.Name == "Rent"));
        Focused(app).Should().Be("Undo Rent paid");
        Press(app, "Undo Rent paid", bills.UndoCommand, bills.Rows.Single(r => r.Name == "Rent"));
        Focused(app).Should().Be("Mark Rent paid");
    }

    [AvaloniaFact]
    public async Task An_error_marks_the_field_it_names_and_moves_focus_there()
    {
        using var app = await Unlocked();
        app.ViewModel.StartSetup(SetupStep.Bills);
        var setup = app.Screen<SetupViewModel>();
        (setup.BillName, setup.BillAmount) = ("Phone", "4five");

        setup.AddBillCommand.Execute(null);
        Harness.Pump();
        var amount = app.Window!.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Amount");
        var marked = (amount.Classes.Contains("invalid"), AutomationProperties.GetHelpText(amount), Focused(app));

        setup.BillAmount = "45";
        setup.AddBillCommand.Execute(null);
        Harness.Pump();

        marked.Should().Be((true, "Amount: \"4five\" isn't an amount.", "Amount"));
        (amount.Classes.Contains("invalid"), AutomationProperties.GetHelpText(amount)).Should().Be((false, null));
    }

    [AvaloniaFact]
    public void Errors_and_notices_are_announced_when_they_change()
    {
        using var app = new Harness();
        var error = new TextBlock { Classes = { "error" } };
        var notice = new TextBlock { Classes = { "notice" } };
        var window = new Window { Content = new StackPanel { Children = { error, notice } } };
        window.Show();

        (AutomationProperties.GetLiveSetting(error), AutomationProperties.GetLiveSetting(notice))
            .Should().Be((AutomationLiveSetting.Assertive, AutomationLiveSetting.Polite));
        window.Close();
    }

    [AvaloniaFact]
    public void The_name_check_catches_an_unnamed_control()
    {
        // Positive control: an icon-only button and an unlabelled text box have nothing to announce.
        var window = new Window
        {
            Content = new StackPanel
            {
                Children =
                {
                    new Button { Content = "Save" },
                    new Button { Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M0,0 L8,8") } },
                    new TextBox(),
                    new TextBox { [AutomationProperties.NameProperty] = "Amount" },
                    new ListBox { ItemsSource = new[] { new Sample("Card") } },
                },
            },
        };
        window.Show();

        Unnamed(window, "Sample").Should().HaveCount(3);
        window.Close();
    }

    private sealed record Sample(string Name);

    private static async Task<Harness> Unlocked()
    {
        var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Populate(app);
        Harness.Pump();
        return app;
    }

    // As a keyboard user would: focus the button, then run what Enter runs.
    private static void Press(Harness app, string name, System.Windows.Input.ICommand command, object? parameter = null)
    {
        Harness.Pump();
        var button = app.Window!.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && (AutomationProperties.GetName(b) == name || b.Content as string == name));
        button.Focus(NavigationMethod.Tab);
        command.Execute(parameter);
        Harness.Pump();
    }

    private static string? Focused(Harness app) =>
        app.Window!.FocusManager?.GetFocusedElement() is Control c
            ? AutomationProperties.GetName(c) is { Length: > 0 } name ? name : (c as ContentControl)?.Content as string
            : null;

    // Accent-coloured pixels just outside a button focused from the keyboard: the outer ring.
    private static int RingPixels(ThemeVariant theme, bool withRing)
    {
        var button = new Button { Content = "Save", Margin = new Thickness(40) };
        if (!withRing)
        {
            button.FocusAdorner = null;
        }

        var window = new Window { Width = 300, Height = 200, RequestedThemeVariant = theme, Content = button };
        window.Show();
        button.Focus(NavigationMethod.Tab);
        Harness.Pump();

        using var frame = window.CaptureRenderedFrame()!;
        var accent = Colour("Accent", theme);
        var bounds = button.Bounds.Translate(button.TranslatePoint(default, window)!.Value - button.Bounds.Position);
        var pixels = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(frame.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length, frame.PixelSize.Width * 4);
        }
        finally
        {
            pinned.Free();
        }

        var (r, b) = frame.Format == PixelFormats.Rgba8888 ? (0, 2) : (2, 0);
        var count = 0;
        for (var y = 0; y < frame.PixelSize.Height; y++)
        {
            for (var x = 0; x < frame.PixelSize.Width; x++)
            {
                var i = ((y * frame.PixelSize.Width) + x) * 4;
                var outside = !bounds.Contains(new Point(x + 0.5, y + 0.5)) && bounds.Inflate(6).Contains(new Point(x + 0.5, y + 0.5));
                if (outside && Near(pixels[i + r], accent.R) && Near(pixels[i + 1], accent.G) && Near(pixels[i + b], accent.B))
                {
                    count++;
                }
            }
        }

        window.Close();
        return count;

        static bool Near(byte a, byte b) => Math.Abs(a - b) <= 12;
    }

    private static IEnumerable<string> Unnamed(Harness app, string where)
    {
        Harness.Pump();
        return Unnamed(app.Window!, where);
    }

    // What UI Automation, and so Narrator, announces for each control that takes focus.
    // Without a name Avalonia falls back to the content's ToString(): a type name or a
    // record's field dump, which is no better than nothing.
    private static IEnumerable<string> Unnamed(Visual root, string where) =>
        root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible)
            .Where(c => ControlAutomationPeer.CreatePeerForElement(c).GetName() is var name
                && (string.IsNullOrWhiteSpace(name) || TypeName().IsMatch(name)
                    || new[] { (c as ContentControl)?.Content, c.DataContext }.Any(o => o is not null and not string && o.ToString() == name)))
            .Select(c => $"{where}: {c.GetType().Name} in {string.Join(" < ", c.GetVisualAncestors().OfType<Control>().Take(4).Select(a => a.GetType().Name))}")
            .Distinct()
            .ToList();

    // Every screen, setup step, option and inline form, with a row in every list.
    private static async Task<List<string>> Tour(Harness app, Func<string, IEnumerable<string>> check, bool smallest = false)
    {
        var found = new List<string>();
        void Check(string where)
        {
            Harness.Pump();
            found.AddRange(check(where));
        }

        app.Open();
        if (smallest)
        {
            (app.Window!.Width, app.Window.Height) = (app.Window.MinWidth, app.Window.MinHeight);
        }

        Check("Welcome");
        app.ViewModel.ShowRestore();
        Check("Restore");
        app.ViewModel.Welcome.StartFreshCommand.Execute(null);
        Check("Create profile");
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, Harness.Passphrase);
        await create.CreateCommand.ExecuteAsync(null);
        Check("Recovery key");

        app.ViewModel.StartSetup();
        var setup = app.Screen<SetupViewModel>();
        foreach (var step in Enum.GetValues<SetupStep>())
        {
            setup.Step = step;
            Check($"Setup {step}");
        }

        // Options that show more fields.
        setup.Step = SetupStep.Paychecks;
        foreach (var pattern in setup.Patterns)
        {
            setup.Pattern = pattern;
            Check($"Setup pay {pattern.Pattern}");
        }

        setup.Step = SetupStep.Bills;
        setup.BillIsEstimate = true;
        Check("Setup bill estimate");
        setup.Step = SetupStep.Debts;
        setup.DebtIsCard = !setup.DebtIsCard;
        Check("Setup other debt kind");

        Populate(app);
        app.ViewModel.OpenApp(Destination.Overview);
        var shell = app.Screen<ShellViewModel>();
        foreach (var destination in Enum.GetValues<Destination>())
        {
            app.ViewModel.OpenApp(destination);
            Check(destination.ToString());
        }

        // The inline forms, open.
        shell.Navigate(Destination.Debts);
        ((DebtsPageViewModel)shell.CurrentPage!).StartConfirmCommand.Execute(null);
        Check("Debts, confirm balance");
        shell.Navigate(Destination.Savings);
        var savings = (SavingsPageViewModel)shell.CurrentPage!;
        savings.StartAddCommand.Execute(null);
        Check("Savings, add goal");
        savings.StartUpdateCommand.Execute(savings.Goals[0]);
        Check("Savings, update saved");
        shell.Navigate(Destination.Transactions);
        var ledger = (TransactionsPageViewModel)shell.CurrentPage!;
        ledger.StartAddCommand.Execute(null);
        Check("Transactions, add entry");
        ledger.StartReconcileCommand.Execute(null);
        Check("Transactions, reconcile");

        app.ViewModel.Lock();
        Check("Unlock");
        return found;
    }

    // A name set on something UI Automation treats as layout is silently dropped, and a text
    // block reports its own text instead. Badges are read as their text; a passphrase is
    // never handed to UI Automation as a value.
    private static List<string> Inert(Visual root, string where) =>
        [.. root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => (Control: c, Name: c is Controls.StatusBadge b ? b.Text : AutomationProperties.GetName(c), Peer: ControlAutomationPeer.CreatePeerForElement(c)))
            .Where(c => (c.Name is { Length: > 0 } && (!c.Peer.IsControlElement() || c.Peer.GetName() != c.Name))
                || (c.Control is TextBox { PasswordChar: not ' ' } && c.Peer is IValueProvider))
            .Select(c => $"{where}: {c.Control.GetType().Name} '{c.Name}' reads as '{c.Peer.GetName()}'{(c.Peer is IValueProvider ? " with its value" : "")}")
            .Distinct()];

    // Each control that takes focus, each badge and each figure either fits or sits in a
    // ScrollViewer that can bring it into view (WCAG 1.4.10 Reflow, 1.4.4 Resize Text).
    // No text spills out of its box, and figures are never trimmed to an ellipsis.
    private static List<string> CutOff(Visual root, string where) =>
        [.. root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0
                && (c.Focusable || c is Controls.StatusBadge or TextBlock))
            .Where(c => c is TextBlock t
                ? t.TextLayout.Width > t.Bounds.Width + 1 || (t.Classes.Contains("mono") && (Unreachable(root, t) || t.TextLayout.TextLines.Any(l => l.HasCollapsed)))
                : Unreachable(root, c))
            .Select(c => $"{where}: {c.GetType().Name} '{Label(c)}' in {string.Join(" < ", c.GetVisualAncestors().OfType<Control>().Take(3).Select(a => a.GetType().Name))}")
            .Distinct()];

    private static string? Label(Control c) => c switch
    {
        TextBlock t => t.Text,
        Controls.StatusBadge b => b.Text,
        _ => AutomationProperties.GetName(c) is { Length: > 0 } name ? name : (c as ContentControl)?.Content?.ToString(),
    };

    private static bool Unreachable(Visual root, Control control)
    {
        var box = new Rect(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);
        foreach (var clip in control.GetVisualAncestors().OfType<Visual>().Where(a => a.ClipToBounds || a == root))
        {
            var area = new Rect(clip.TranslatePoint(default, root)!.Value, clip.Bounds.Size);

            // Scrolling brings the control into the viewport along each axis the viewer scrolls,
            // as long as it lies within what the viewer can scroll to.
            if (clip is ScrollContentPresenter { CanHorizontallyScroll: true } across)
            {
                if (box.Right > area.X + across.Extent.Width + 1)
                {
                    return true;
                }

                box = new Rect(area.X, box.Y, Math.Min(box.Width, area.Width), box.Height);
            }

            if (clip is ScrollContentPresenter { CanVerticallyScroll: true } down)
            {
                if (box.Bottom > area.Y + down.Extent.Height + 1)
                {
                    return true;
                }

                box = new Rect(box.X, area.Y, box.Width, Math.Min(box.Height, area.Height));
            }

            if (box.Right > area.Right + 1 || box.Bottom > area.Bottom + 1)
            {
                return true;
            }

            if (clip == root)
            {
                break;
            }
        }

        return false;
    }

    // Every colour token, and the colours Fluent's own controls are drawn from, that
    // doesn't come from the user's contrast theme.
    private static List<string> Leaks(ContrastPalette p)
    {
        var app = Avalonia.Application.Current!;
        var tokens = app.Resources.MergedDictionaries
            .Select(r => r is ResourceInclude include ? include.Loaded : r).OfType<ResourceDictionary>()
            .Select(r => r.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var dark) ? dark as ResourceDictionary : null)
            .OfType<ResourceDictionary>()
            .SelectMany(d => d.Keys.OfType<string>())
            .ToList();
        string[] fluent = ["SystemAccentColor", "SystemAltHighColor", "SystemBaseHighColor", "SystemBaseLowColor", "SystemChromeMediumColor", "SystemChromeDisabledLowColor", "SystemRegionColor", "SystemListLowColor"];
        Color[] theirs = [p.Window, p.WindowText, p.Highlight, p.HighlightText, p.HotLight, p.GrayText];
        return [.. tokens.Concat(fluent)
            .Select(key => (key, Value: app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value switch { ISolidColorBrush b => b.Color, Color c => c, _ => (Color?)null } : null))
            .Where(r => r.Value is not { } colour || !theirs.Contains(colour))
            .Select(r => $"{r.key} is {r.Value?.ToString() ?? "missing"}")];
    }

    // A profile with a row in every list, so every row template renders.
    private static void Populate(Harness app)
    {
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var checking = entry.AddCashAccount("Checking", 1_240m);
        entry.AddCashAccount("Savings", 1_850m, kind: AccountKind.Savings);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, today, 7.4m, 285m, 28), checking.Id);
        entry.AddCard(new CardEntry("Card", 2_300m, today, 24.99m, 150m, 21, 3), checking.Id);
        entry.SetSavings(150m, 300m);
        entry.AddGoal("Car fund", 3_000m, 1_250m, today.AddMonths(6));
        entry.RecordEntry(checking.Id, "Groceries", -64.20m, today);
    }

    [GeneratedRegex(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$")]
    private static partial Regex TypeName();

    private static ThemeVariant Variant(string theme) => theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static List<string> Failures(IEnumerable<(string Fore, string Back, string? Tint, double Needs)> pairs, ThemeVariant theme) =>
        [.. pairs.Select(p =>
            {
                var back = Colour(p.Back, theme);
                var behind = p.Tint is { } tint ? Over(Colour(tint, theme), back) : back;
                return (p, Ratio: Contrast(Colour(p.Fore, theme), behind));
            })
            .Where(r => r.Ratio < r.p.Needs)
            .Select(r => $"{theme}: {r.p.Fore} on {r.p.Tint ?? ""}{(r.p.Tint is null ? "" : " over ")}{r.p.Back} is {r.Ratio:0.00}:1, needs {r.p.Needs}:1")];

    private static Color Colour(string key, ThemeVariant theme) =>
        Avalonia.Application.Current!.TryGetResource(key, theme, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"No colour token {key}.");

    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round((a * t) + ((1 - a) * u));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) => v / 255.0 is var s && s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
