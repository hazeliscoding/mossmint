using Avalonia.Headless.XUnit;
using Mossmint.Application.Setup;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

public class ScenariosPageTests
{
    [AvaloniaFact]
    public async Task Compares_four_ways_to_pay_with_the_same_extra()
    {
        using var app = await WithDebts();

        var page = Open(app);

        page.Columns.Select(c => c.Title).Should().Equal("Today's plan", "Avalanche", "Snowball", "Your order");
        page.Columns[0].Status.Should().Be("Safe");

        page.Extra = 100m;
        page.Refresh();

        page.ExtraText.Should().Be("100.00");
        page.Columns[1].Steps[0].Should().StartWith("1. Card");
        page.Columns[2].Steps[0].Should().StartWith("1. Card");
        page.Columns[1].DebtFree.Should().NotBe(page.Columns[0].DebtFree);
    }

    [AvaloniaFact]
    public async Task An_extra_is_safe_up_to_the_largest_safe_amount_and_not_a_cent_more()
    {
        using var app = await WithDebts();
        var page = Open(app);

        page.UseLargestCommand.Execute(null);

        page.Extra.Should().Be(page.Largest!.Value);
        page.Columns.Skip(1).Should().OnlyContain(c => c.Status == "Safe");

        page.ExtraText = (page.Largest.Value + 0.01m).ToString("N2", System.Globalization.CultureInfo.CurrentCulture);
        page.Refresh();

        page.ExtraError.Should().BeNull();
        page.Columns.Skip(1).Should().OnlyContain(c => c.Status == "Below cushion");
        page.Columns[0].Status.Should().Be("Safe");
    }

    [AvaloniaFact]
    public async Task Moving_a_debt_changes_the_custom_order()
    {
        using var app = await WithDebts();
        var page = Open(app);
        var first = page.CustomOrder[0];

        page.MoveLaterCommand.Execute(first);

        page.CustomOrder[1].Should().Be(first);
        page.Columns[3].Steps[0].Should().StartWith("1. " + page.CustomOrder[0].Name);
    }

    [AvaloniaFact]
    public async Task A_typo_in_the_amount_is_refused_and_keeps_the_last_comparison()
    {
        using var app = await WithDebts();
        var page = Open(app);

        page.ExtraText = "fifty";

        page.ExtraError.Should().Contain("Extra each month");
        page.Extra.Should().Be(0m);
    }

    [AvaloniaFact]
    public async Task Without_debts_the_page_offers_to_add_one()
    {
        using var app = await Unlocked();
        new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System).AddCashAccount("Checking", 1_000m);

        var page = Open(app);

        (page.HasNoDebts, page.HasComparison).Should().Be((true, false));
    }

    private static ScenariosPageViewModel Open(Harness app)
    {
        app.ViewModel.Shell.Navigate(Destination.Scenarios);
        return (ScenariosPageViewModel)app.ViewModel.Shell.CurrentPage!;
    }

    private static async Task<Harness> WithDebts()
    {
        var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var checking = entry.AddCashAccount("Checking", 1_240m);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 1_150m, 1), checking.Id);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, today, 7.4m, 285m, 28), checking.Id);
        entry.AddCard(new CardEntry("Card", 2_300m, today, 24.99m, 150m, 21, 3), checking.Id);
        entry.SetSavings(150m, 300m);
        return app;
    }

    private static async Task<Harness> Unlocked()
    {
        var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return app;
    }
}
