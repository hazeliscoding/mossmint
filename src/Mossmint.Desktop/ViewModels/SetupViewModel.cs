using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Services;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Planning;

namespace Mossmint.Desktop.ViewModels;

public enum SetupStep
{
    Cash,
    Paychecks,
    Bills,
    Debts,
    Savings,
    Done,
}

public sealed record PatternOption(PayPattern Pattern, string Label);

public sealed record SetupStepItem(SetupStep Step, string Label, bool IsCurrent);

// "Start fresh": one short step at a time, each skippable (docs/design/user-journeys.md).
// Every save goes straight into the encrypted profile through ManualEntry.
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly ManualEntry _entry;
    private readonly Action _finished;
    private readonly CultureInfo _culture;
    private AccountId? _cashAccount;

    public SetupViewModel(AppServices services, Action finished, CultureInfo? culture = null)
    {
        _entry = new ManualEntry(services.Context.Current.Data, services.Clock);
        (_finished, _culture) = (finished, culture ?? CultureInfo.CurrentCulture);
        _cashAccount = services.Context.Current.Data.Accounts().FirstOrDefault(a => a.Kind == AccountKind.Checking)?.Id;
        var today = services.Clock.GetLocalNow().Date;
        _cashAsOf = today;
        _nextPayday = today;
    }

    private static readonly (SetupStep Step, string Label)[] StepLabels =
    [
        (SetupStep.Cash, "1. Cash"), (SetupStep.Paychecks, "2. Paychecks"), (SetupStep.Bills, "3. Bills"),
        (SetupStep.Debts, "4. Debts"), (SetupStep.Savings, "5. Savings"), (SetupStep.Done, "6. Done"),
    ];

    public IReadOnlyList<SetupStepItem> Steps => [.. StepLabels.Select(s => new SetupStepItem(s.Step, s.Label, s.Step == Step))];

    public IReadOnlyList<PatternOption> Patterns { get; } =
    [
        new(PayPattern.TwiceMonthly, "Twice a month (15th and last day)"),
        new(PayPattern.EveryTwoWeeks, "Every two weeks"),
        new(PayPattern.Weekly, "Every week"),
        new(PayPattern.MonthlyOnDay, "Once a month, on a day"),
        new(PayPattern.LastBusinessDay, "Last business day of the month"),
    ];

    public ObservableCollection<string> Added { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsPaychecks), nameof(IsBills), nameof(IsDebts), nameof(IsSavings), nameof(IsDone), nameof(StepTitle), nameof(Steps))]
    private SetupStep _step = SetupStep.Cash;

    [ObservableProperty]
    private string? _error;

    // Cash
    [ObservableProperty]
    private string _cashName = "Checking";

    [ObservableProperty]
    private string _cashBalance = "";

    [ObservableProperty]
    private DateTime? _cashAsOf;

    // Paychecks
    [ObservableProperty]
    private string _payName = "Paycheck";

    [ObservableProperty]
    private string _netPay = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsNextPayday), nameof(NeedsPayDay))]
    private PatternOption? _pattern;

    [ObservableProperty]
    private DateTime? _nextPayday;

    [ObservableProperty]
    private decimal? _payDay = 1;

    [ObservableProperty]
    private bool _payEarly = true;

    // Bills
    [ObservableProperty]
    private string _billName = "";

    [ObservableProperty]
    private string _billAmount = "";

    [ObservableProperty]
    private decimal? _billDueDay = 1;

    [ObservableProperty]
    private bool _billDueLastDay;

    [ObservableProperty]
    private bool _billIsEstimate;

    [ObservableProperty]
    private string _billLow = "";

    [ObservableProperty]
    private string _billHigh = "";

    // Debts
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebtIsLoan))]
    private bool _debtIsCard;

    public bool DebtIsLoan
    {
        get => !DebtIsCard;
        set => DebtIsCard = !value;
    }

    [ObservableProperty]
    private string _debtName = "";

    [ObservableProperty]
    private string _debtBalance = "";

    [ObservableProperty]
    private string _debtApr = "";

    [ObservableProperty]
    private string _debtPayment = "";

    [ObservableProperty]
    private decimal? _debtDueDay = 1;

    [ObservableProperty]
    private decimal? _debtClosingDay = 21;

    // Savings
    [ObservableProperty]
    private string _savingsPerPaycheck = "";

    [ObservableProperty]
    private string _cushion = "";

    public bool IsCash => Step == SetupStep.Cash;
    public bool IsPaychecks => Step == SetupStep.Paychecks;
    public bool IsBills => Step == SetupStep.Bills;
    public bool IsDebts => Step == SetupStep.Debts;
    public bool IsSavings => Step == SetupStep.Savings;
    public bool IsDone => Step == SetupStep.Done;
    public bool NeedsNextPayday => Pattern?.Pattern is PayPattern.EveryTwoWeeks or PayPattern.Weekly;
    public bool NeedsPayDay => Pattern?.Pattern == PayPattern.MonthlyOnDay;

    public string StepTitle => Step switch
    {
        SetupStep.Cash => "Where your money lands",
        SetupStep.Paychecks => "When you get paid",
        SetupStep.Bills => "What you pay every month",
        SetupStep.Debts => "Loans and cards",
        SetupStep.Savings => "Savings and cushion",
        _ => "You're set up",
    };

    [RelayCommand]
    private void SaveAndContinue()
    {
        if (Try(SaveStep))
        {
            Next();
        }
    }

    [RelayCommand]
    private void Skip()
    {
        Error = null;
        Next();
    }

    [RelayCommand]
    private void Back()
    {
        Error = null;
        Step = Step == SetupStep.Cash ? SetupStep.Cash : Step - 1;
    }

    [RelayCommand]
    private void AddBill() => Try(SaveBill);

    [RelayCommand]
    private void AddDebt() => Try(SaveDebt);

    private void SaveBill()
    {
        var amount = MoneyInput.Required(BillAmount, _culture, "Amount");
        var bill = _entry.AddBill(new BillEntry(
            BillName, amount, BillDueLastDay ? 0 : (int)(BillDueDay ?? 0), BillPurpose.Expense, BillIsEstimate,
            BillIsEstimate ? MoneyInput.Optional(BillLow, _culture, "Low") : null,
            BillIsEstimate ? MoneyInput.Optional(BillHigh, _culture, "High") : null), _cashAccount);
        Added.Add($"Bill · {bill.Name}");
        (BillName, BillAmount, BillLow, BillHigh, BillIsEstimate, BillDueLastDay) = ("", "", "", "", false, false);
    }

    private void SaveDebt()
    {
        var asOf = DateOnly.FromDateTime(CashAsOf ?? DateTime.Today);
        var balance = MoneyInput.Required(DebtBalance, _culture, "Balance owed");
        var apr = ParsePercent(DebtApr);
        var payment = MoneyInput.Optional(DebtPayment, _culture, "Payment") ?? 0m;
        var due = (int)(DebtDueDay ?? 0);
        var name = DebtName.Trim();
        if (DebtIsCard)
        {
            _entry.AddCard(new CardEntry(DebtName, balance, asOf, apr, payment, (int)(DebtClosingDay ?? 0), due), _cashAccount);
            Added.Add($"Card · {name}");
        }
        else
        {
            _entry.AddLoan(new LoanEntry(DebtName, balance, asOf, apr, payment, due), _cashAccount);
            Added.Add($"Loan · {name}");
        }

        (DebtName, DebtBalance, DebtApr, DebtPayment) = ("", "", "", "");
    }

    [RelayCommand]
    private void Finish() => _finished();

    private void SaveStep()
    {
        switch (Step)
        {
            case SetupStep.Cash:
                var account = _entry.AddCashAccount(CashName, MoneyInput.Required(CashBalance, _culture, "Balance"),
                    DateOnly.FromDateTime(CashAsOf ?? DateTime.Today));
                _cashAccount = account.Id;
                Added.Add($"Account · {account.Name}");
                break;
            case SetupStep.Paychecks:
                var pattern = Pattern?.Pattern ?? throw new ArgumentException("Choose how often you're paid.");
                var income = _entry.AddPaycheck(new PaycheckEntry(
                    PayName, MoneyInput.Required(NetPay, _culture, "Net pay"), pattern,
                    NextPayday is { } next ? DateOnly.FromDateTime(next) : null, (int?)PayDay, PayEarly), _cashAccount);
                Added.Add($"Paycheck · {income.Name}");
                break;
            case SetupStep.Bills:
                if (!string.IsNullOrWhiteSpace(BillName) || !string.IsNullOrWhiteSpace(BillAmount))
                {
                    SaveBill();
                }

                break;
            case SetupStep.Debts:
                if (!string.IsNullOrWhiteSpace(DebtName) || !string.IsNullOrWhiteSpace(DebtBalance))
                {
                    SaveDebt();
                }

                break;
            case SetupStep.Savings:
                _entry.SetSavings(MoneyInput.Optional(SavingsPerPaycheck, _culture, "Savings per paycheck"), MoneyInput.Optional(Cushion, _culture, "Cushion"));
                Added.Add("Savings and cushion");
                break;
        }
    }

    private decimal ParsePercent(string text) =>
        decimal.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Number, _culture, out var value) && value is >= 0m and < 1000m
            ? value
            : throw new ArgumentException("APR: enter a percentage like 24.99.");

    private bool Try(Action action)
    {
        try
        {
            action();
            Error = null;
            return true;
        }
        catch (ArgumentException e)
        {
            Error = e.Message;
            return false;
        }
    }

    private void Next() => Step = Step == SetupStep.Done ? SetupStep.Done : Step + 1;
}
