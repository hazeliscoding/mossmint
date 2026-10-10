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
    public async Task Without_a_cash_account_the_forecast_pages_say_what_to_add()
    {
        using var app = await Unlocked();

        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        var page = (CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!;

        page.Rows.Should().BeEmpty();
        page.Unavailable.Should().Contain("checking account");
        app.ViewModel.Shell.ForecastAsOf.Should().Be("—");
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
