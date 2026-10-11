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
    public async Task Harsher_assumptions_shrink_the_safe_extra_and_show_how_late_debt_free_could_be()
    {
        using var app = await WithDebts();
        var page = Open(app);
        var expected = page.Largest!.Value;

        page.Spending = page.SpendingChoices.Single(c => c.Label == "Stress");
        page.LatePaychecks = true;

        page.Largest.Should().BeLessThan(expected);
        page.Columns.Should().OnlyContain(c => !c.HasTightNote);

        page.Spending = page.SpendingChoices[0];
        page.LatePaychecks = false;
        page.Extra = expected;
        page.Refresh();

        page.Columns[1].TightNote.Should().StartWith("With stress spending and late paychecks");
        page.Columns[0].HasTightNote.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Applying_shows_what_changes_saves_the_plan_and_can_be_undone()
    {
        using var app = await WithDebts();
        var page = Open(app);
        page.Extra = 150m;
        page.Refresh();

        page.StartApplyCommand.Execute(page.Columns[1]);

        page.IsConfirming.Should().BeTrue();
        page.Changes.Select(c => c.What).Should().Contain(["Extra each month", "Debt-free", "Safe to spend now"]);
        page.Changes.Single(c => c.What == "Extra each month").Should().Be(new ChangeRow("Extra each month", "None", "$150.00"));
        page.HasRisk.Should().BeFalse();
        app.Services.Context.Current.Data.Settings().DebtPlan.Should().BeNull("nothing is saved before Apply");

        page.ApplyPlanCommand.Execute(null);

        page.IsConfirming.Should().BeFalse();
        page.Notice.Should().Contain("Avalanche");
        page.Columns[0].Title.Should().Be("Your plan");
        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        ((CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!).Rows.Should().Contain(r => r.Events.Contains("Extra debt payment"));

        app.ViewModel.Shell.Navigate(Destination.Scenarios);
        page.CanUndo.Should().BeTrue();
        page.UndoApplyCommand.Execute(null);

        app.Services.Context.Current.Data.Settings().DebtPlan.Should().BeNull();
        page.Columns[0].Title.Should().Be("Today's plan");
        page.CanUndo.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task An_extra_above_the_safe_amount_warns_before_it_is_applied()
    {
        using var app = await WithDebts();
        var page = Open(app);
        page.Extra = page.Largest!.Value + 0.01m;
        page.Refresh();

        page.StartApplyCommand.Execute(page.Columns[2]);

        (page.HasRisk, page.RiskLabel, page.ApplyLabel).Should().Be((true, "Below cushion", "Apply anyway"));
        page.Risk.Should().Contain("below the");

        page.CancelApplyCommand.Execute(null);

        page.IsConfirming.Should().BeFalse();
        app.Services.Context.Current.Data.Settings().DebtPlan.Should().BeNull();
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
        entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
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
