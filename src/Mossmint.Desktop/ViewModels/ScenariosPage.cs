using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
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
    string? Problem)
{
    public bool HasProblem => Problem is not null;

    public string AccessibleName => $"{Title}: {Subtitle}. {DebtFreeLabel} {DebtFree}. Interest {Interest}. Lowest cash {Lowest}. {Status}.";
}

public sealed record OrderedDebt(AccountId Account, string Name);

public sealed partial class ScenariosPageViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly IAppNavigator _navigator;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _syncing;

    public ScenariosPageViewModel(AppServices services, IAppNavigator navigator)
        : base(Destination.Scenarios, "Scenarios", "The same extra each month, paid off in different orders. Nothing here changes your plan.")
    {
        (_services, _navigator) = (services, navigator);

        // A comparison takes a moment, so moving the slider recomputes once it settles.
        _settle.Tick += (_, _) => Refresh();
    }

    public ObservableCollection<ScenarioColumn> Columns { get; } = [];

    public ObservableCollection<OrderedDebt> CustomOrder { get; } = [];

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

    public override void OnNavigatedTo() => Refresh();

    // The settle timer calls this once the amount stops changing.
    public void Refresh()
    {
        _settle.Stop();
        Columns.Clear();
        (Unavailable, Largest, LargestText, LargestBasis) = (null, null, "", "");
        if (!_services.Context.IsUnlocked)
        {
            OnPropertyChanged(nameof(HasComparison));
            return;
        }

        var data = _services.Context.Current.Data;
        var today = DateOnly.FromDateTime(_services.Clock.GetLocalNow().DateTime);
        var outcome = Scenarios.Compare(data, today, new Money(Extra, data.Settings().Currency), [.. CustomOrder.Select(d => d.Account)]);
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

    private void Show(PayoffComparison comparison)
    {
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
                $"Keeps checking at or above the {Describe.Money(comparison.Cushion)} cushion every day through {comparison.Through:MMM yyyy}, paid from the first paycheck each month."),
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
            option.Problem);
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
