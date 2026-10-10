using Avalonia.Headless.XUnit;
using Mossmint.Application.Setup;
using Mossmint.Desktop.ViewModels;

namespace Mossmint.Desktop.SmokeTests;

public class ForecastPageTests
{
    [AvaloniaFact]
    public async Task Cash_flow_lists_every_day_in_the_chosen_range()
    {
        using var app = await Unlocked();
        new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System).AddCashAccount("Checking", 1_000m);

        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        var page = (CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!;

        page.Rows.Should().HaveCount(45);
        page.Days = 90;
        page.Rows.Should().HaveCount(90);
        page.Unavailable.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task Paychecks_show_what_each_paycheck_pays_for()
    {
        using var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_000m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Rent", 900m, 1), checking.Id);

        app.ViewModel.Shell.Navigate(Destination.Paychecks);
        var page = (PaychecksPageViewModel)app.ViewModel.Shell.CurrentPage!;

        page.Schedules.Should().ContainSingle().Which.Name.Should().Be("Paycheck");
        // Any 45 days hold at least two paydays after the cash on hand, or three from a payday.
        page.Cards.Count.Should().BeGreaterThanOrEqualTo(3);
        page.Cards.Should().ContainSingle(c => c.Lines.Any(l => l.Name == "Rent"));
    }

    [AvaloniaFact]
    public async Task Without_a_cash_account_the_forecast_pages_say_what_to_add()
    {
        using var app = await Unlocked();

        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        var page = (CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!;

        page.Rows.Should().BeEmpty();
        page.Unavailable.Should().Contain("checking account");
        app.ViewModel.Shell.ForecastAsOf.Should().Be("—");

        app.ViewModel.Shell.Navigate(Destination.Paychecks);
        var paychecks = (PaychecksPageViewModel)app.ViewModel.Shell.CurrentPage!;
        (paychecks.Cards.Count, paychecks.HasSchedules).Should().Be((0, false));
        paychecks.Unavailable.Should().Contain("checking account");
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
