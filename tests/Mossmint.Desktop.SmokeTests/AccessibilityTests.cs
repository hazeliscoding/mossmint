using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Mossmint.Application.Setup;
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

    [AvaloniaFact]
    public async Task Every_focusable_control_has_a_name_narrator_can_read()
    {
        using var app = new Harness();
        app.Open();
        var unnamed = Unnamed(app, "Welcome").ToList();

        app.ViewModel.ShowRestore();
        unnamed.AddRange(Unnamed(app, "Restore"));
        app.ViewModel.Welcome.StartFreshCommand.Execute(null);
        unnamed.AddRange(Unnamed(app, "Create profile"));
        var create = app.Screen<CreateProfileViewModel>();
        (create.Name, create.Passphrase, create.Confirm) = ("Juniper", Harness.Passphrase, Harness.Passphrase);
        await create.CreateCommand.ExecuteAsync(null);
        unnamed.AddRange(Unnamed(app, "Recovery key"));

        app.ViewModel.StartSetup();
        var setup = app.Screen<SetupViewModel>();
        foreach (var step in Enum.GetValues<SetupStep>())
        {
            setup.Step = step;
            unnamed.AddRange(Unnamed(app, $"Setup {step}"));
        }

        Populate(app);
        app.ViewModel.OpenApp(Destination.Overview);
        var shell = app.Screen<ShellViewModel>();
        foreach (var destination in Enum.GetValues<Destination>())
        {
            app.ViewModel.OpenApp(destination);
            unnamed.AddRange(Unnamed(app, destination.ToString()));
        }

        // The inline forms, open.
        shell.Navigate(Destination.Debts);
        ((DebtsPageViewModel)shell.CurrentPage!).StartConfirmCommand.Execute(null);
        unnamed.AddRange(Unnamed(app, "Debts, confirm balance"));
        shell.Navigate(Destination.Savings);
        var savings = (SavingsPageViewModel)shell.CurrentPage!;
        savings.StartAddCommand.Execute(null);
        unnamed.AddRange(Unnamed(app, "Savings, add goal"));
        savings.StartUpdateCommand.Execute(savings.Goals[0]);
        unnamed.AddRange(Unnamed(app, "Savings, update saved"));
        shell.Navigate(Destination.Transactions);
        var ledger = (TransactionsPageViewModel)shell.CurrentPage!;
        ledger.StartAddCommand.Execute(null);
        unnamed.AddRange(Unnamed(app, "Transactions, add entry"));
        ledger.StartReconcileCommand.Execute(null);
        unnamed.AddRange(Unnamed(app, "Transactions, reconcile"));

        app.ViewModel.Lock();
        unnamed.AddRange(Unnamed(app, "Unlock"));

        string.Join(Environment.NewLine, unnamed).Should().BeEmpty();
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

    // A profile with a row in every list, so every row template renders.
    private static void Populate(Harness app)
    {
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var checking = entry.AddCashAccount("Checking", 1_240m);
        entry.AddCashAccount("Savings", 1_850m, kind: AccountKind.Savings);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
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
