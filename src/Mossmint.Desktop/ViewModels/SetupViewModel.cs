using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
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

public sealed record SetupStepItem(SetupStep Step, string Label, bool IsCurrent, bool IsSaved, string AccessibleName);

// "Start fresh": one short step at a time, each skippable (docs/design/user-journeys.md).
// Every save goes straight into the encrypted profile through ManualEntry. An empty page
// can open a single step, which then goes straight to Done.
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly ManualEntry _entry;
    private readonly Action _finished;
    private readonly CultureInfo _culture;
    private readonly SetupStep? _only;
    private readonly HashSet<SetupStep> _saved = [];

    // What a one-item step added, so going back to it shows that instead of adding it twice.
    private readonly Dictionary<SetupStep, string> _notes = [];
    private AccountId? _cashAccount;

    public SetupViewModel(AppServices services, Action finished, SetupStep? only = null, CultureInfo? culture = null)
    {
        _services = services;
        _entry = new ManualEntry(services.Context.Current.Data, services.Clock);
        (_finished, _only, _culture) = (finished, only, culture ?? CultureInfo.CurrentCulture);
        _cashAccount = services.Context.Current.Data.Accounts().FirstOrDefault(a => a.Kind == AccountKind.Checking)?.Id;
        var today = services.Clock.GetLocalNow().Date;
        _cashAsOf = today;
        _nextPayday = today;
        _step = only ?? SetupStep.Cash;
    }

    private static readonly (SetupStep Step, string Label)[] StepLabels =
    [
        (SetupStep.Cash, "1. Cash"), (SetupStep.Paychecks, "2. Paychecks"), (SetupStep.Bills, "3. Bills"),
        (SetupStep.Debts, "4. Debts"), (SetupStep.Savings, "5. Savings"), (SetupStep.Done, "6. Done"),
    ];

    public IReadOnlyList<SetupStepItem> Steps => [.. StepLabels.Select(s => new SetupStepItem(
        s.Step, s.Label, s.Step == Step, _saved.Contains(s.Step),
        s.Step == SetupStep.Done
            ? "Done"
            : $"{s.Label[3..]}, step {(int)s.Step + 1} of 5{(s.Step == Step ? ", current step" : _saved.Contains(s.Step) ? ", added" : "")}"))];

    public IReadOnlyList<PatternOption> Patterns { get; } =
    [
        new(PayPattern.TwiceMonthly, "Twice a month (15th and last day)"),
        new(PayPattern.EveryTwoWeeks, "Every two weeks"),
        new(PayPattern.Weekly, "Every week"),
        new(PayPattern.MonthlyOnDay, "Once a month, on a day"),
        new(PayPattern.LastBusinessDay, "Last business day of the month"),
    ];

    public ObservableCollection<string> Added { get; } = [];

    // The Done step's first look at the forecast: Safe to spend and the lowest point.
    public ObservableCollection<MetricCard> Results { get; } = [];

    [ObservableProperty]
    private string? _missing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsPaychecks), nameof(IsBills), nameof(IsDebts), nameof(IsSavings), nameof(IsDone), nameof(StepTitle), nameof(Steps))]
    [NotifyPropertyChangedFor(nameof(SavedNote), nameof(IsSaved), nameof(ShowCashForm), nameof(ShowPaycheckForm), nameof(PrimaryText), nameof(CanGoBack))]
    private SetupStep _step;

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
    public string? SavedNote => _notes.GetValueOrDefault(Step);
    public bool IsSaved => SavedNote is not null;
    public bool ShowCashForm => IsCash && !IsSaved;
    public bool ShowPaycheckForm => IsPaychecks && !IsSaved;
    public string PrimaryText => IsSaved ? "Continue" : "Save and continue";
    public bool CanGoBack => _only is null && Step != SetupStep.Cash;
    public bool IsFullRun => _only is null;
    public string FinishText => _only is null ? "Go to Overview" : "Done";
    public bool NeedsNextPayday => Pattern?.Pattern is PayPattern.EveryTwoWeeks or PayPattern.Weekly;
    public bool NeedsPayDay => Pattern?.Pattern == PayPattern.MonthlyOnDay;

    public string StepTitle => Step switch
    {
        SetupStep.Cash => "Where your money lands",
        SetupStep.Paychecks => "When you get paid",
        SetupStep.Bills => "What you pay every month",
        SetupStep.Debts => "Loans and cards",
        SetupStep.Savings => "Savings and cushion",
        _ => _only is null ? "You're set up" : "Saved",
    };

    [RelayCommand]
    private void SaveAndContinue()
    {
        if (IsSaved || Try(SaveStep))
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
        Step = CanGoBack ? Step - 1 : Step;
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
            BillIsEstimate ? MoneyInput.Optional(BillLow, _culture, "Lowest usual amount") : null,
            BillIsEstimate ? MoneyInput.Optional(BillHigh, _culture, "Highest usual amount") : null), _cashAccount);
        Added.Add($"Bill · {bill.Name}");
        _saved.Add(SetupStep.Bills);
        OnPropertyChanged(nameof(Steps));
        (BillName, BillAmount, BillLow, BillHigh, BillIsEstimate, BillDueLastDay) = ("", "", "", "", false, false);
    }

    private void SaveDebt()
    {
        var asOf = DateOnly.FromDateTime(CashAsOf ?? DateTime.Today);
        var balance = MoneyInput.Required(DebtBalance, _culture, "Balance owed");
        var apr = ParsePercent(DebtApr);
        var payment = MoneyInput.Optional(DebtPayment, _culture, "Monthly payment") ?? 0m;
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

        _saved.Add(SetupStep.Debts);
        OnPropertyChanged(nameof(Steps));

        (DebtName, DebtBalance, DebtApr, DebtPayment) = ("", "", "", "");
    }

    [RelayCommand]
    private void Finish() => _finished();

    private void SaveStep()
    {
        switch (Step)
        {
            case SetupStep.Cash:
                var balance = MoneyInput.Required(CashBalance, _culture, "Balance");
                var asOf = DateOnly.FromDateTime(CashAsOf ?? DateTime.Today);
                var account = _entry.AddCashAccount(CashName, balance, asOf);
                _cashAccount = account.Id;
                Added.Add($"Account · {account.Name}");
                _saved.Add(SetupStep.Cash);
                _notes[SetupStep.Cash] = $"{account.Name}, {Describe.Money(new Money(balance, account.Currency))} as of {asOf:d MMM}. To correct it later, reconcile the balance on Transactions.";
                break;
            case SetupStep.Paychecks:
                var pattern = Pattern?.Pattern ?? throw new ArgumentException("Choose how often you're paid.");
                var income = _entry.AddPaycheck(new PaycheckEntry(
                    PayName, MoneyInput.Required(NetPay, _culture, "Net pay"), pattern,
                    NextPayday is { } next ? DateOnly.FromDateTime(next) : null, (int?)PayDay, PayEarly), _cashAccount);
                Added.Add($"Paycheck · {income.Name}");
                _saved.Add(SetupStep.Paychecks);
                _notes[SetupStep.Paychecks] = $"{income.Name}, {Describe.Money(income.NetPay)} on {Describe.Schedule(income.PayDates)}.";
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
                _entry.SetSavings(MoneyInput.Optional(SavingsPerPaycheck, _culture, "Save from each paycheck"), MoneyInput.Optional(Cushion, _culture, "Cushion"));
                if (_saved.Add(SetupStep.Savings))
                {
                    Added.Add("Savings and cushion");
                }

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

    private void Next()
    {
        if (Step != SetupStep.Done)
        {
            Step = _only is not null ? SetupStep.Done : Step + 1;
        }
    }

    partial void OnStepChanged(SetupStep value)
    {
        if (value != SetupStep.Done)
        {
            return;
        }

        Results.Clear();
        var today = DateOnly.FromDateTime(_services.Clock.GetLocalNow().DateTime);
        switch (Forecasts.Build(_services.Context.Current.Data, today))
        {
            case ProfileForecast forecast:
                Missing = null;
                foreach (var card in OverviewCards.For(forecast).Take(2))
                {
                    Results.Add(card);
                }

                break;
            case ForecastUnavailable unavailable:
                Missing = string.Join(" ", unavailable.Missing);
                break;
        }
    }
}
