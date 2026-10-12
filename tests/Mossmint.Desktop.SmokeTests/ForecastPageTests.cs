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
    public async Task The_forecast_shown_is_kept_in_the_journal_once_a_day()
    {
        using var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_000m, PayPattern.TwiceMonthly), checking.Id);

        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        app.ViewModel.Shell.Navigate(Destination.Overview);

        var journal = app.Services.Context.Current.Data.ForecastJournal();
        journal.Should().ContainSingle().Which.MadeOn.Should().Be(DateOnly.FromDateTime(DateTime.Now));
        journal[0].Account.Should().Be(checking.Id);
    }

    [AvaloniaFact]
    public async Task Cash_flow_reruns_under_stress_spending_and_late_paychecks()
    {
        using var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_000m, PayPattern.TwiceMonthly), checking.Id);
        entry.AddBill(new BillEntry("Electric", 70m, 12, IsEstimate: true, Low: 60m, High: 95m), checking.Id);
        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        var page = (CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!;
        var paydays = page.Rows.Where(r => r.Status == "Payday").Select(r => r.Date).ToList();

        page.LatePaychecks = true;

        page.Rows.Where(r => r.Status == "Payday").Select(r => r.Date).Should().NotEqual(paydays);
        page.Summary.Should().Contain("paychecks 3 business days late");

        page.Spending = page.SpendingChoices.Single(c => c.Label == "Stress");

        page.Summary.Should().Contain("Stress spending");
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
    public async Task Marking_a_bill_paid_moves_the_forecast_and_undo_moves_it_back()
    {
        using var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        entry.AddBill(new BillEntry("Rent", 900m, 1), checking.Id);
        app.ViewModel.Shell.Navigate(Destination.Bills);
        var bills = (BillsPageViewModel)app.ViewModel.Shell.CurrentPage!;

        bills.MarkPaidCommand.Execute(bills.Rows.Single());

        var rent = bills.Rows.Single();
        (rent.Status, rent.CanMarkPaid, rent.CanUndo).Should().Be(("Paid", false, true));
        bills.Notice.Should().Contain("Rent marked paid");
        Opening(app).Should().Be("$100.00");

        bills.UndoCommand.Execute(rent);

        bills.Rows.Single().CanMarkPaid.Should().BeTrue();
        Opening(app).Should().Be("$1,000.00");
    }

    [AvaloniaFact]
    public async Task Confirming_a_debt_balance_replaces_an_old_one()
    {
        using var app = await Unlocked();
        var entry = new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System);
        var checking = entry.AddCashAccount("Checking", 1_000m);
        var today = DateOnly.FromDateTime(DateTime.Now);
        entry.AddLoan(new LoanEntry("Car loan", 9_800m, today.AddDays(-60), 7.4m, 285m, 28), checking.Id);
        app.ViewModel.Shell.Navigate(Destination.Debts);
        var debts = (DebtsPageViewModel)app.ViewModel.Shell.CurrentPage!;

        debts.Selected!.Status.Should().Be("Stale");
        debts.Selected.HasWarning.Should().BeTrue();
        debts.StartConfirmCommand.Execute(null);
        debts.BalanceText = "nine thousand";
        debts.SaveBalanceCommand.Execute(null);
        debts.FormError.Should().Contain("Balance owed");

        debts.BalanceText = "9,500.00";
        debts.InterestText = "12.40";
        debts.SaveBalanceCommand.Execute(null);

        (debts.Selected!.Balance, debts.Selected.Status, debts.Selected.HasWarning, debts.IsConfirming).Should().Be(("$9,500.00", "Confirmed", false, false));
        debts.Selected.Facts.Should().Contain(f => f.Label == "Unpaid interest" && f.Value == "$12.40");
        debts.Notice.Should().Contain("Car loan");
    }

    [AvaloniaFact]
    public async Task Savings_goals_can_be_added_and_updated()
    {
        using var app = await Unlocked();
        app.ViewModel.Shell.Navigate(Destination.Savings);
        var savings = (SavingsPageViewModel)app.ViewModel.Shell.CurrentPage!;
        savings.HasGoals.Should().BeFalse();

        savings.StartAddCommand.Execute(null);
        (savings.GoalName, savings.GoalTarget) = ("Trip", "lots");
        savings.SaveGoalCommand.Execute(null);
        savings.FormError.Should().Contain("Goal amount");

        (savings.GoalTarget, savings.GoalSaved) = ("900", "120");
        savings.SaveGoalCommand.Execute(null);

        var trip = savings.Goals.Single();
        (trip.Status, savings.IsAdding).Should().Be(("No date", false));
        savings.StartUpdateCommand.Execute(trip);
        savings.SavedText = "900.00";
        savings.SaveSavedCommand.Execute(null);
        (savings.Goals.Single().Status, savings.IsUpdating).Should().Be(("Reached", false));
    }

    [AvaloniaFact]
    public async Task Entries_change_todays_cash_and_reconciling_keeps_the_difference()
    {
        using var app = await Unlocked();
        new ManualEntry(app.Services.Context.Current.Data, TimeProvider.System).AddCashAccount("Checking", 1_000m);
        app.ViewModel.Shell.Navigate(Destination.Transactions);
        var ledger = (TransactionsPageViewModel)app.ViewModel.Shell.CurrentPage!;
        ledger.Account!.Name.Should().Be("Checking");

        ledger.StartAddCommand.Execute(null);
        (ledger.EntryDescription, ledger.EntryAmount) = ("Groceries", "115");
        ledger.SaveEntryCommand.Execute(null);

        ledger.Notice.Should().Contain("$885.00");
        Opening(app).Should().Be("$885.00");
        app.ViewModel.Shell.Navigate(Destination.Transactions);
        ledger.Rows.First().Should().Match<LedgerRow>(r => r.Description == "Groceries" && r.Amount == "−$115.00" && r.CanVoid);

        ledger.StartReconcileCommand.Execute(null);
        ledger.ExpectedText.Should().Contain("$885.00");
        ledger.BankBalance = "863.50";
        ledger.SaveReconcileCommand.Execute(null);

        ledger.Notice.Should().Contain("$21.50 less");
        ledger.Rows.First().Status.Should().Be("Differs");
        Opening(app).Should().Be("$863.50");

        app.ViewModel.Shell.Navigate(Destination.Transactions);
        ledger.VoidCommand.Execute(ledger.Rows.Single(r => r.Description == "Groceries"));
        ledger.Rows.Single(r => r.Description == "Groceries").Status.Should().Be("Void");
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

    [AvaloniaFact]
    public async Task The_overview_chart_links_to_its_table()
    {
        using var app = await Unlocked();
        app.ViewModel.OpenApp(Destination.Overview);

        ((OverviewPageViewModel)app.ViewModel.Shell.CurrentPage!).ShowTableCommand.Execute(null);

        app.ViewModel.Shell.CurrentPage.Should().BeOfType<CashFlowPageViewModel>();
    }

    // Steps away first: navigating to the page already open doesn't refresh it.
    private static string Opening(Harness app)
    {
        app.ViewModel.Shell.Navigate(Destination.Overview);
        app.ViewModel.Shell.Navigate(Destination.CashFlow);
        var opening = ((CashFlowPageViewModel)app.ViewModel.Shell.CurrentPage!).Forecast!.Opening;
        return Describe.Money(opening);
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
