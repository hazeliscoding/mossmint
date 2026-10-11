using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Profiles;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.ViewModels;

// One way of paying, as a column of the comparison.
public sealed record ScenarioColumn(
    string Title,
    string Subtitle,
    string DebtFreeLabel,
    string DebtFree,
    string Interest,
    string Lowest,
    string Status,
    StatusTone Tone,
    IReadOnlyList<string> Steps,
    string? Problem,
    string? TightNote,
    PayoffOrder Order)
{
    public bool HasProblem => Problem is not null;

    public bool CanApply => Order != PayoffOrder.TodaysPlan;

    public bool HasTightNote => TightNote is not null;

    public string AccessibleName => $"{Title}: {Subtitle}. {DebtFreeLabel} {DebtFree}. Interest {Interest}. Lowest cash {Lowest}. {Status}. {TightNote}";
}

public sealed record OrderedDebt(AccountId Account, string Name);

// One line of the summary shown before a plan is applied.
public sealed record ChangeRow(string What, string Now, string After)
{
    public string AccessibleName => $"{What}: now {Now}, after {After}.";
}

public sealed partial class ScenariosPageViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly IAppNavigator _navigator;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _syncing;
    private bool _seeded;
    private IProfileSession? _session;
    private Money _cushion;
    private DebtPlan? _applied;
    private DebtPlan? _pending;
    private DebtPlan? _replaced;

    public ScenariosPageViewModel(AppServices services, IAppNavigator navigator)
        : base(Destination.Scenarios, "Scenarios", "The same extra each month, paid off in different orders. Nothing changes your plan until you apply one.")
    {
        (_services, _navigator) = (services, navigator);

        // A comparison takes a moment, so moving the slider recomputes once it settles.
        _settle.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<ScenarioColumn> Columns { get; } = [];

    public ObservableCollection<OrderedDebt> CustomOrder { get; } = [];

    public ObservableCollection<ChangeRow> Changes { get; } = [];

    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private string _confirmTitle = "";

    [ObservableProperty]
    private string _nextExtrasText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRisk))]
    private string? _risk;

    [ObservableProperty]
    private string _riskLabel = "";

    [ObservableProperty]
    private StatusTone _riskTone;

    [ObservableProperty]
    private string _applyLabel = "Apply plan";

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _canUndo;

    public bool HasRisk => Risk is not null;

    public bool HasComparison => Columns.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderValue))]
    private decimal _extra;

    [ObservableProperty]
    private string _extraText = "0.00";

    [ObservableProperty]
    private string? _extraError;

    [ObservableProperty]
    private double _sliderMaximum = 500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseLargest))]
    private decimal? _largest;

    [ObservableProperty]
    private string _largestText = "";

    [ObservableProperty]
    private string _largestBasis = "";

    [ObservableProperty]
    private string? _unavailable;

    [ObservableProperty]
    private bool _hasNoDebts;

    public bool CanUseLargest => Largest is not null;

    public IReadOnlyList<SpendingChoice> SpendingChoices => SpendingChoice.All;

    [ObservableProperty]
    private SpendingChoice _spending = SpendingChoice.All[0];

    [ObservableProperty]
    private bool _latePaychecks;

    partial void OnSpendingChanged(SpendingChoice value) => Refresh();

    partial void OnLatePaychecksChanged(bool value) => Refresh();

    // The slider moves in steps of 5; the box takes any amount.
    public double SliderValue
    {
        get => (double)Extra;
        set => Extra = Math.Round((decimal)value / 5m, MidpointRounding.AwayFromZero) * 5m;
    }

    partial void OnExtraChanged(decimal value)
    {
        if (!_syncing)
        {
            _syncing = true;
            (ExtraText, ExtraError) = (value.ToString("N2", _culture), null);
            _syncing = false;
        }

        _settle.Stop();
        _settle.Start();
    }

    partial void OnExtraTextChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        if (MoneyInput.TryParse(value, _culture, out var amount) && amount >= 0m)
        {
            _syncing = true;
            (ExtraError, Extra) = (null, decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
            _syncing = false;
        }
        else
        {
            ExtraError = $"Extra each month: \"{value}\" isn't an amount of zero or more.";
        }
    }

    [RelayCommand]
    private void UseLargest()
    {
        if (Largest is { } amount)
        {
            Extra = amount;
            Refresh();
        }
    }

    [RelayCommand]
    private void MoveEarlier(OrderedDebt debt) => Move(debt, -1);

    [RelayCommand]
    private void MoveLater(OrderedDebt debt) => Move(debt, 1);

    [RelayCommand]
    private void AddDebts() => _navigator.StartSetup(SetupStep.Debts, Destination.Scenarios);

    [RelayCommand]
    private void StartApply(ScenarioColumn column)
    {
        if (!_services.Context.IsUnlocked || !column.CanApply)
        {
            return;
        }

        var data = _services.Context.Current.Data;
        var custom = column.Order == PayoffOrder.Custom ? CustomOrder.Select(d => d.Account).ToList() : [];
        var plan = new DebtPlan(new Money(Extra, data.Settings().Currency), column.Order, custom);
        if (Scenarios.Preview(data, Today, plan, Assumptions) is not { } preview)
        {
            return;
        }

        _pending = plan;
        ConfirmTitle = $"Apply {PlanName(plan)}?";
        Changes.Clear();
        foreach (var row in Rows(preview))
        {
            Changes.Add(row);
        }

        NextExtrasText = preview.NextExtras.Count == 0 || plan.MonthlyExtra.IsZero
            ? "No extra goes out; each paid-off debt's payment moves to the next one."
            : $"The extra goes out with the first paycheck of each month: {string.Join(", ", preview.NextExtras.Select(d => d.ToString("d MMM", _culture)))} and on.";
        (Risk, RiskLabel, RiskTone) = preview.After.Status switch
        {
            ExtraStatus.BelowCushion => ($"With this plan, checking would drop to {Describe.Money(preview.After.Lowest)} on {preview.After.LowestDate.ToString("d MMM yyyy", _culture)}, below the {Describe.Money(_cushion)} cushion.",
                "Below cushion", StatusTone.Critical),
            ExtraStatus.NeedsConfirmation => ($"Mossmint can't check this plan against your cash yet. {LargestBasis}", "Needs confirmation", StatusTone.Warning),
            _ => ((string?)null, "", StatusTone.Neutral),
        };
        ApplyLabel = HasRisk ? "Apply anyway" : "Apply plan";
        IsConfirming = true;
    }

    [RelayCommand]
    private void ApplyPlan()
    {
        StayWithProfile();
        if (_pending is not { } plan || !_services.Context.IsUnlocked)
        {
            return;
        }

        _replaced = Scenarios.Apply(_services.Context.Current.Data, plan);
        CanUndo = true;
        Notice = $"{PlanName(plan)} applied. Cash Flow, Paychecks, Debts and Overview now follow it.";
        Refresh();
    }

    [RelayCommand]
    private void CancelApply()
    {
        (IsConfirming, _pending) = (false, null);
        Changes.Clear();
    }

    // No timer: undo stays until the next plan is applied.
    [RelayCommand]
    private void UndoApply()
    {
        StayWithProfile();
        if (!CanUndo || !_services.Context.IsUnlocked)
        {
            return;
        }

        Scenarios.Apply(_services.Context.Current.Data, _replaced);
        Notice = _replaced is { } back ? $"{PlanName(back)} is back." : "Plan removed. Each debt is back on its own payments.";
        (_replaced, CanUndo) = (null, false);
        Refresh();
    }

    // The first visit starts from the saved plan's extra.
    public override void OnNavigatedTo()
    {
        StayWithProfile();
        if (!_seeded && _services.Context.IsUnlocked && _services.Context.Current.Data.Settings().DebtPlan is { } plan)
        {
            Extra = plan.MonthlyExtra.Amount;
        }

        _seeded = true;
        Refresh();
    }

    // The settle timer calls this once the amount stops changing.
    public void Refresh()
    {
        _settle.Stop();
        CancelApply();
        Columns.Clear();
        (Unavailable, Largest, LargestText, LargestBasis) = (null, null, "", "");
        if (!_services.Context.IsUnlocked)
        {
            OnPropertyChanged(nameof(HasComparison));
            return;
        }

        var data = _services.Context.Current.Data;
        var settings = data.Settings();
        IReadOnlyList<AccountId>? custom = CustomOrder.Count > 0 ? [.. CustomOrder.Select(d => d.Account)] : settings.DebtPlan?.Custom;
        var outcome = Scenarios.Compare(data, Today, new Money(Extra, settings.Currency), custom, Assumptions);
        HasNoDebts = data.Debts().Count == 0;
        if (outcome is PayoffComparison comparison)
        {
            Show(comparison);
        }
        else if (!HasNoDebts && outcome is ForecastUnavailable unavailable)
        {
            Unavailable = string.Join(" ", unavailable.Missing);
        }

        OnPropertyChanged(nameof(HasComparison));
    }

    // The page outlives a profile session: after a lock, whatever it held for the
    // last profile (the extra, the order, a plan waiting to be applied, the plan
    // undo would restore) is dropped, so none of it can be written to another one.
    private void StayWithProfile()
    {
        var session = _services.Context.IsUnlocked ? _services.Context.Current : null;
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        _session = session;
        (_seeded, _applied, _replaced, CanUndo, Notice) = (false, null, null, false, null);
        CancelApply();
        CustomOrder.Clear();
        Extra = 0m;
    }

    private DateOnly Today => DateOnly.FromDateTime(_services.Clock.GetLocalNow().DateTime);

    private ForecastAssumptions Assumptions => new(Spending.Value, LatePaychecks);

    private void Show(PayoffComparison comparison)
    {
        (_applied, _cushion) = (comparison.Applied, comparison.Cushion);

        // Keep the user's order; debts added since go at the end.
        var custom = comparison.Options.Single(o => o.Order == PayoffOrder.Custom).Steps;
        if (!CustomOrder.Select(d => d.Account).SequenceEqual(custom.Select(s => s.Account)))
        {
            CustomOrder.Clear();
            foreach (var step in custom)
            {
                CustomOrder.Add(new OrderedDebt(step.Account, step.Name));
            }
        }

        foreach (var option in comparison.Options)
        {
            Columns.Add(Column(option));
        }

        var largest = comparison.LargestSafeExtra;
        (Largest, LargestText, LargestBasis) = largest.State switch
        {
            SafeToSpendState.Safe => (largest.Amount!.Value.Amount, Describe.Money(largest.Amount.Value),
                $"Keeps checking at or above the {Describe.Money(comparison.Cushion)} cushion every day through {comparison.Through:MMM yyyy}, paid from the first paycheck each month. {SpendingChoice.Describe(Spending, LatePaychecks)}."),
            SafeToSpendState.Shortfall => ((decimal?)null, "None",
                $"Checking already falls {Describe.Money(largest.Amount!.Value)} below the {Describe.Money(comparison.Cushion)} cushion with no extra, so no extra is safe."),
            _ => (null, "Needs confirmation", string.Join(" ", largest.Reasons)),
        };
        SliderMaximum = Largest is { } amount ? (double)Math.Max(500m, Math.Ceiling(amount * 1.5m / 50m) * 50m) : 500;
    }

    private ScenarioColumn Column(PayoffOption option)
    {
        var (title, subtitle) = option.Order switch
        {
            PayoffOrder.TodaysPlan when _applied is { } plan => ("Your plan", $"{PlanName(plan)}, saved"),
            PayoffOrder.TodaysPlan => ("Today's plan", "No extra; each debt on its own payments"),
            PayoffOrder.Avalanche => ("Avalanche", "Highest rate first"),
            PayoffOrder.Snowball => ("Snowball", "Smallest balance first"),
            _ => ("Your order", "The order you set below"),
        };
        var (status, tone) = option.Status switch
        {
            ExtraStatus.Safe => ("Safe", StatusTone.Healthy),
            ExtraStatus.BelowCushion => ("Below cushion", StatusTone.Critical),
            _ => ("Needs confirmation", StatusTone.Warning),
        };
        return new ScenarioColumn(
            title,
            subtitle,
            option.IsApproximate ? "Debt-free, approx." : "Debt-free",
            option.DebtFree is { } date ? Month(date) : "No date",
            option.Interest is { } interest ? Describe.Money(interest) : "Unknown",
            Describe.Money(option.Lowest),
            status,
            tone,
            [.. option.Steps.Select((s, i) => $"{i + 1}. {s.Name} · {(s.PaidOff is { } paid ? Month(paid) : "not paid off")}")],
            option.Problem,
            TightNote(option),
            option.Order);
    }

    private string PlanName(DebtPlan plan) =>
        $"{plan.Order switch { PayoffOrder.Avalanche => "Avalanche", PayoffOrder.Snowball => "Snowball", _ => "Your order" }} with {Describe.Money(plan.MonthlyExtra)} a month";

    private List<ChangeRow> Rows(PlanPreview preview)
    {
        var (before, after) = (preview.Before, preview.After);
        string Amount(Money extra) => extra.IsZero ? "None" : Describe.Money(extra);
        string Date(DateOnly? date) => date is { } d ? Month(d) : "No date";
        string Order(PayoffOption option) => string.Join(", then ", option.Steps.Select(s => s.Name));
        string Safe(SafeToSpendView view) => view.State switch
        {
            SafeToSpendState.Safe => Describe.Money(view.Amount!.Value),
            SafeToSpendState.Shortfall => $"Short by {Describe.Money(view.Amount!.Value)}",
            _ => "Needs confirmation",
        };

        List<ChangeRow> rows =
        [
            new("Extra each month", Amount(before.Extra), Amount(after.Extra)),
            new("Order", _applied is null ? "Each debt on its own" : Order(before), Order(after)),
        ];
        rows.AddRange(after.Steps.Select(step =>
            new ChangeRow($"{step.Name} paid off", Date(before.Steps.FirstOrDefault(s => s.Account == step.Account)?.PaidOff), Date(step.PaidOff))));
        rows.Add(new("Debt-free", Date(before.DebtFree), Date(after.DebtFree)));
        rows.Add(new("Interest", before.Interest is { } was ? Describe.Money(was) : "Unknown", after.Interest is { } will ? Describe.Money(will) : "Unknown"));
        rows.Add(new("Safe to spend now", Safe(preview.SafeBefore), Safe(preview.SafeAfter)));
        return rows;
    }

    // The far end of the debt-free range: what stress spending and late paychecks would leave room for.
    private string? TightNote(PayoffOption option)
    {
        if (option.ExtraIfTight is not { } tight)
        {
            return null;
        }

        var room = tight.IsZero ? "no extra fits" : $"only {Describe.Money(tight)} a month fits";
        var date = option.DebtFreeIfTight is { } late ? $"debt-free {Month(late)}" : "no debt-free date";
        return $"With stress spending and late paychecks, {room}: {date}.";
    }

    private string Month(DateOnly date) => date.ToString("MMM yyyy", _culture);

    private void Move(OrderedDebt debt, int by)
    {
        var (from, to) = (CustomOrder.IndexOf(debt), CustomOrder.IndexOf(debt) + by);
        if (from >= 0 && to >= 0 && to < CustomOrder.Count)
        {
            CustomOrder.Move(from, to);
            Refresh();
        }
    }
}
