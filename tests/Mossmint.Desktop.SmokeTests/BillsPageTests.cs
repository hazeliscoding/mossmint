using Avalonia.Headless.XUnit;
using Mossmint.Application.Setup;
using Mossmint.Desktop.ViewModels;
using Mossmint.Domain.Planning;

namespace Mossmint.Desktop.SmokeTests;

// The test user's questions: a price change, a real billing day, a cancellation, a typo.
public class BillsPageTests
{
    [AvaloniaFact]
    public async Task A_price_change_and_a_new_billing_day_are_saved_from_the_bills_page()
    {
        using var app = await WithBills();
        var page = Open(app);

        page.StartEditCommand.Execute(page.Rows.Single(r => r.Name == "Streaming"));

        (page.IsEditing, page.EditName, page.EditAmount, page.EditDay, page.CanEnd).Should().Be((true, "Streaming", "13.99", 20m, true));

        page.EditAmount = "15.99";
        page.EditDay = 14;
        page.SaveEditCommand.Execute(null);

        page.IsEditing.Should().BeFalse();
        var row = page.Rows.Single(r => r.Name == "Streaming");
        (row.Amount, row.Cadence).Should().Be(("$15.99", "monthly on the 14th"));
        page.Notice.Should().Contain("Streaming");
    }

    [AvaloniaFact]
    public async Task A_cancelled_bill_ends_and_a_typo_is_removed()
    {
        using var app = await WithBills();
        var page = Open(app);

        page.StartEditCommand.Execute(page.Rows.Single(r => r.Name == "Gmy"));
        page.RemoveBillCommand.Execute(null);
        page.StartEditCommand.Execute(page.Rows.Single(r => r.Name == "Streaming"));
        page.EndFrom = DateTime.Today;
        page.EndBillCommand.Execute(null);

        page.Rows.Should().NotContain(r => r.Name == "Gmy");
        page.Rows.Single(r => r.Name == "Streaming").Detail.Should().StartWith("Ended");
        page.IsEditing.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task A_bad_edit_says_why_and_changes_nothing()
    {
        using var app = await WithBills();
        var page = Open(app);
        page.StartEditCommand.Execute(page.Rows.Single(r => r.Name == "Streaming"));

        page.EditAmount = "lots";
        page.SaveEditCommand.Execute(null);

        (page.IsEditing, page.FormError).Should().Be((true, "Amount: \"lots\" isn't an amount."));
        page.Rows.Single(r => r.Name == "Streaming").Amount.Should().Be("$13.99");
    }

    [AvaloniaFact]
    public async Task A_debt_payment_can_be_changed_but_not_ended_or_removed()
    {
        using var app = await WithBills();
        var page = Open(app);

        page.StartEditCommand.Execute(page.Rows.Single(r => r.View.Bill.Purpose == BillPurpose.DebtPayment));

        page.CanEnd.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task A_bill_can_be_added_while_others_exist()
    {
        using var app = await WithBills();
        var page = Open(app);

        page.IsEmpty.Should().BeFalse();
        page.AddBillsCommand.CanExecute(null).Should().BeTrue();
    }

    private static BillsPageViewModel Open(Harness app)
    {
        app.ViewModel.Shell.Navigate(Destination.Bills);
        return (BillsPageViewModel)app.ViewModel.Shell.CurrentPage!;
    }

    private static async Task<Harness> WithBills()
    {
        var app = new Harness();
        app.CreateProfile();
        app.Open();
        var unlock = app.Screen<UnlockViewModel>();
        unlock.Passphrase = Harness.Passphrase;
        await unlock.UnlockCommand.ExecuteAsync(null);
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddBill(new BillEntry("Streaming", 13.99m, 20), checking.Id);
        entry.AddBill(new BillEntry("Gmy", 40m, 12), checking.Id);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, DateOnly.FromDateTime(DateTime.Now), 7.4m, 285m, 28), checking.Id);
        return app;
    }
}
