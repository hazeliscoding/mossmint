using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Data;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;

namespace Mossmint.Desktop.ViewModels;

public sealed record DebtFact(string Label, string Value, string Note);

// One debt as a selectable card, with the facts its detail panel shows.
public sealed record DebtCard(
    AccountId Account,
    string Name,
    string Type,
    string Balance,
    string AsOf,
    string Status,
    StatusTone Tone,
    double? PaidShare,
    string PaidText,
    string Rate,
    string Payment,
    string Payoff,
    string? Warning,
    IReadOnlyList<DebtFact> Facts,
    string Source,
    bool IsLoan)
{
    public bool HasProgress => PaidShare is not null;

    public bool HasWarning => Warning is not null;

    public string AccessibleName => $"{Name}, {Type}. {Balance} owed, {AsOf}. {Status}. Payoff {Payoff}.";
}

public sealed partial class DebtsPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Debts, "Debts", "Balances, terms and payoff dates. Extra-payment comparisons come with Scenarios in version 0.5.")
{
    // The empty page's way in: that one setup step, then back here.
    [RelayCommand]
    private void AddDebts() => navigator.StartSetup(SetupStep.Debts, Destination.Debts);

    // Older than one statement cycle, a balance no longer says much about today.
    private const int StaleAfterDays = 31;

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ObservableCollection<DebtCard> Debts { get; } = [];

    public bool IsEmpty => Debts.Count == 0;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DebtCard? _selected;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _isConfirming;

    [ObservableProperty]
    private string _balanceText = "";

    [ObservableProperty]
    private string _interestText = "";

    [ObservableProperty]
    private string? _formError;

    public bool HasSelection => Selected is not null;

    public override void OnNavigatedTo()
    {
        Notice = null;
        Load(Selected?.Account);
    }

    partial void OnSelectedChanged(DebtCard? value) => CancelConfirm();

    [RelayCommand]
    private void StartConfirm()
    {
        (BalanceText, InterestText, FormError) = ("", "", null);
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        FormError = null;
    }

    [RelayCommand]
    private void SaveBalance()
    {
        if (Selected is not { } debt)
        {
            return;
        }

        try
        {
            var balance = MoneyInput.Required(BalanceText, _culture, "Balance owed");
            var interest = debt.IsLoan ? MoneyInput.Optional(InterestText, _culture, "Unpaid interest") : null;
            var snapshot = new ManualEntry(services.Context.Current.Data, services.Clock).ConfirmBalance(debt.Account, balance, interest);
            Notice = $"{debt.Name}: {Describe.Money(snapshot.Balance)} confirmed today. Its payoff date now starts from it.";
            IsConfirming = false;
            Load(debt.Account);
        }
        catch (ArgumentException e)
        {
            FormError = e.Message;
        }
    }

    private void Load(AccountId? keep)
    {
        Debts.Clear();
        Summary = "";
        if (services.Context.IsUnlocked)
        {
            var data = services.Context.Current.Data;
            var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
            var views = Forecasts.Build(data, today) is ProfileForecast f ? f.Debts.ToDictionary(d => d.Account) : [];
            var accounts = data.Accounts().ToDictionary(a => a.Id);
            var bills = data.Bills();
            foreach (var debt in data.Debts())
            {
                Debts.Add(Card(debt, accounts.GetValueOrDefault(debt.Account)?.Name ?? "Debt", data, bills, views.GetValueOrDefault(debt.Account), today));
            }

            Summary = SummaryText(data, today);
        }

        Selected = Debts.FirstOrDefault(d => d.Account == keep) ?? Debts.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string SummaryText(IProfileData data, DateOnly today)
    {
        var balances = Debts.Select(d => data.Balances(d.Account).FirstOrDefault()).OfType<BalanceSnapshot>().ToList();
        var owed = balances.Count > 0 ? Describe.Money(balances.Select(b => b.Balance).Aggregate((a, b) => a + b)) : "—";
        var unconfirmed = Debts.Count(d => d.Tone != StatusTone.Healthy);
        return $"{Plural(Debts.Count, "debt")} · {owed} owed" + (unconfirmed > 0 ? $" · {Plural(unconfirmed, "balance")} to confirm" : "");
    }

    private static DebtCard Card(DebtAccount debt, string name, IProfileData data, IReadOnlyList<RecurringBill> bills, DebtView? view, DateOnly today)
    {
        var latest = data.Balances(debt.Account).FirstOrDefault();
        var bill = bills.FirstOrDefault(b => b.TargetAccount == debt.Account && b.Purpose == BillPurpose.DebtPayment);
        var (type, model, rate) = debt.Terms switch
        {
            SimpleInterestLoanTerms t => ("Loan", "daily simple interest", Apr(t.Rates)),
            AmortizingLoanTerms a => ("Loan", "amortizing", Apr(a.Rates)),
            CreditCardTerms c => ("Card", "card, payoff is approximate", Apr(c.Buckets[0].Rates)),
            _ => ("Debt", "", "—"),
        };
        var payment = bill?.Amount.Expected ?? debt.Terms switch
        {
            SimpleInterestLoanTerms t => t.RegularPayment,
            AmortizingLoanTerms a => a.ScheduledPayment,
            _ => null,
        };
        var schedule = bill?.DueDates ?? (debt.Terms as SimpleInterestLoanTerms)?.PaymentDates ?? (debt.Terms as AmortizingLoanTerms)?.PaymentDates;

        var age = latest?.Provenance.AgeInDays(today);
        var (status, tone) = latest is null ? ("No balance", StatusTone.Warning)
            : latest.Provenance.Confidence < Confidence.Confirmed ? Describe.Confidence(latest.Provenance.Confidence)
            : age > StaleAfterDays ? ("Stale", StatusTone.Warning)
            : ("Confirmed", StatusTone.Healthy);
        var warning = latest is null ? "There's no balance yet. Confirm one to see a payoff date."
            : latest.Provenance.Confidence < Confidence.Confirmed ? $"This balance was {SourceText(latest.Provenance.Source)} and hasn't been confirmed. The payoff date is estimated from it."
            : age > StaleAfterDays ? $"This balance is {age} days old. The payoff date is estimated from it; confirm a current one to update it."
            : null;

        double? share = debt.OriginalPrincipal is { IsPositive: true } original && latest is not null
            ? (double)Math.Clamp(1m - (latest.Balance.Amount / original.Amount), 0m, 1m)
            : null;
        var payoff = view?.PayoffDate is { } date ? date.ToString("MMM yyyy", CultureInfo.CurrentCulture) + (view.IsApproximate ? " (approx.)" : "") : "—";

        List<DebtFact> facts =
        [
            new("Balance owed", latest is null ? "—" : Describe.Money(latest.Balance), latest is null ? "not entered" : $"as of {latest.Provenance.AsOf:d MMM yyyy} · {Describe.Confidence(latest.Provenance.Confidence).Status}"),
        ];
        if (latest?.AccruedInterest is { IsZero: false } accrued)
        {
            facts.Add(new("Unpaid interest", Describe.Money(accrued), "on top of the balance"));
        }

        facts.Add(new("Rate", rate, model));
        facts.Add(new("Payment", payment is { } p ? Describe.Money(p) : "—", schedule is null ? "no payment planned" : Describe.Schedule(schedule)));
        facts.Add(new("Payoff", payoff, view?.Problem ?? "with regular payments only; a projection, not a payoff quote"));
        facts.Add(new("Interest left", view?.InterestLeft is { } interest ? Describe.Money(interest) : "—", "until payoff"));

        return new DebtCard(
            debt.Account,
            name,
            type,
            latest is null ? "—" : Describe.Money(latest.Balance),
            latest is null ? "no balance yet" : $"as of {latest.Provenance.AsOf:d MMM}",
            status,
            tone,
            share,
            share is { } s && debt.OriginalPrincipal is { } o ? $"{s:P0} of {Describe.Money(o)} paid off" : "",
            rate,
            payment is { } amount ? Describe.Money(amount) : "—",
            payoff,
            warning,
            facts,
            latest is null ? "" : $"Source: {SourceText(latest.Provenance.Source)}" + (latest.Provenance.Reference is { } r ? $" · {r}" : ""),
            debt.Terms is not CreditCardTerms);
    }

    private static string Apr(RateSchedule rates) => $"{rates.Segments[^1].Rate.Percent.ToString("0.00", CultureInfo.CurrentCulture)}% APR";

    private static string SourceText(SourceKind source) => source switch
    {
        SourceKind.UserConfirmed => "entered by you",
        SourceKind.LenderStatement => "from a lender statement",
        SourceKind.Imported => "imported",
        SourceKind.Connector => "from a bank feed",
        _ => "estimated",
    };

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
