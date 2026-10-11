using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Desktop.Controls;
using Mossmint.Domain;

namespace Mossmint.Desktop.ViewModels;

public sealed record PaycheckLine(string When, string Name, string Amount);

// One funding period as a card: what came in, what it pays, what's left and how low cash gets.
public sealed record PaycheckCard(
    string Title,
    string Subtitle,
    string Value,
    string ValueNote,
    IReadOnlyList<PaycheckLine> Lines,
    string Leftover,
    string Lowest,
    string LowestNote,
    string Status,
    StatusTone Tone)
{
    public string AccessibleName => $"{Title}, {Value} {ValueNote}. {Subtitle}. Leftover {Leftover}. Lowest {Lowest}, {LowestNote}. {Status}.";
}

public sealed partial class PaychecksPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Paychecks, "Paychecks", "What each paycheck pays for, and the lowest cash before the next one.")
{
    // The empty page's way in: that one setup step, then back here.
    [RelayCommand]
    private void AddPaycheck() => navigator.StartSetup(SetupStep.Paychecks, Destination.Paychecks);

    [ObservableProperty]
    private string? _unavailable;

    [ObservableProperty]
    private string _plan = "";

    public ObservableCollection<RecordRow> Schedules { get; } = [];

    public ObservableCollection<PaycheckCard> Cards { get; } = [];

    public bool HasSchedules => Schedules.Count > 0;

    public bool HasCards => Cards.Count > 0;

    public override void OnNavigatedTo()
    {
        Schedules.Clear();
        Cards.Clear();
        Unavailable = null;
        Plan = "";
        if (services.Context.IsUnlocked)
        {
            var data = services.Context.Current.Data;
            foreach (var income in data.Income())
            {
                var (status, tone) = Describe.Confidence(income.Provenance.Confidence);
                Schedules.Add(new RecordRow(income.Name, Describe.Schedule(income.PayDates), Describe.Money(income.NetPay), status, tone));
            }

            var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
            switch (Forecasts.Build(data, today))
            {
                case ProfileForecast f:
                    Plan = PlanText(f);
                    foreach (var period in f.Paychecks)
                    {
                        Cards.Add(Card(period, f));
                    }

                    break;
                case ForecastUnavailable u:
                    Unavailable = string.Join(" ", u.Missing);
                    break;
            }
        }

        OnPropertyChanged(nameof(HasSchedules));
        OnPropertyChanged(nameof(HasCards));
    }

    private static string PlanText(ProfileForecast f)
    {
        var target = f.Paychecks.FirstOrDefault(p => p.Payday is not null)?.SavingsTarget;
        var savings = target is { IsZero: false } t ? $"{Describe.Money(t)} goes to savings from each paycheck once its bills are paid" : "No savings set aside per paycheck";
        return $"Paid into {f.AccountName}. {savings}. {Describe.Money(f.Cushion)} stays in {f.AccountName} as the cushion.";
    }

    private static PaycheckCard Card(PaycheckView p, ProfileForecast f)
    {
        var lines = new List<PaycheckLine>();
        if (p.Payday is not null)
        {
            lines.Add(new("CARRIED", "Left from before this paycheck", Signed(p.CarryIn)));
        }

        lines.AddRange(p.Funded.Select(b => new PaycheckLine($"{b.Due:d MMM}".ToUpperInvariant(), b.Label, "−" + Describe.Money(b.Amount))));
        if (!p.SavingsTarget.IsZero)
        {
            var shortBy = p.SavingsTarget - p.SavingsFunded;
            lines.Add(new("SAVINGS", shortBy.IsZero ? "To savings" : $"To savings, {Describe.Money(shortBy)} short of {Describe.Money(p.SavingsTarget)}", "−" + Describe.Money(p.SavingsFunded)));
        }

        var from = p.Payday ?? f.Days[0].Date;
        var covers = $"{from:d MMM}–{p.Until:d MMM} · {(p.BillCount == 1 ? "1 bill" : $"{p.BillCount} bills")}";
        var (status, tone) = p.Leftover.IsNegative ? ("Short", StatusTone.Critical)
            : p.Lowest < f.Cushion ? ("Below cushion", StatusTone.Critical)
            : p.SavingsFunded < p.SavingsTarget ? ("Savings short", StatusTone.Warning)
            : ("Covered", StatusTone.Healthy);

        return new PaycheckCard(
            p.Payday is { } payday ? $"{payday:ddd d MMM}" : "Cash on hand",
            p.Payday is null ? $"Pays what's due {covers}" : $"Covers {covers}",
            Describe.Money(p.Payday is null ? p.CarryIn : p.NetPay),
            p.Payday is null ? $"in {f.AccountName}" : "net pay",
            lines,
            Describe.Money(p.Leftover),
            Describe.Money(p.Lowest),
            $"{p.LowestDate:ddd d MMM} · cushion {Describe.Money(f.Cushion)}",
            status,
            tone);
    }

    private static string Signed(Money money) => money.IsNegative ? Describe.Money(money) : "+" + Describe.Money(money);
}
