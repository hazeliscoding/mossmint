using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Controls;
using Mossmint.Domain.Planning;

namespace Mossmint.Desktop.ViewModels;

// A bill and its next due date in the forecast window, paid or not.
public sealed record BillLine(BillView View, string Name, string Detail, string Cadence, string Amount, string Status, StatusTone Tone)
{
    public BillDue? Next => View.Upcoming.FirstOrDefault();

    public bool CanMarkPaid => Next is { Payment: null } && View.Bill.Amount.Expected is not null;

    public bool CanUndo => Next?.Payment is not null;

    public string MarkPaidName => $"Mark {Name} paid";

    public string UndoName => $"Undo {Name} paid";
}

public sealed partial class BillsPageViewModel(AppServices services)
    : PageViewModel(Destination.Bills, "Bills", "Recurring bills and payments. Mark one paid when the money leaves your account.")
{
    public ObservableCollection<BillLine> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty]
    private string? _notice;

    // The rows are rebuilt after Mark paid or Undo; focus goes to the same row's other button.
    [ObservableProperty]
    private string? _focusTarget;

    public override void OnNavigatedTo()
    {
        (Notice, FocusTarget) = (null, null);
        Load();
    }

    [RelayCommand]
    private void MarkPaid(BillLine line)
    {
        try
        {
            var payment = Entry().MarkPaid(line.View.Bill, line.Next!.Nominal);
            Notice = $"{line.Name} marked paid: {Describe.Money(payment.Amount)} today. It stays off the balance until you confirm a newer one.";
        }
        catch (ArgumentException e)
        {
            Notice = e.Message;
        }

        Load();
        FocusTarget = line.UndoName;
    }

    [RelayCommand]
    private void Undo(BillLine line)
    {
        Entry().Undo(line.Next!.Payment!);
        Notice = $"{line.Name} is unpaid again.";
        Load();
        FocusTarget = line.MarkPaidName;
    }

    private ManualEntry Entry() => new(services.Context.Current.Data, services.Clock);

    private void Load()
    {
        Rows.Clear();
        if (services.Context.IsUnlocked)
        {
            var today = DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);
            foreach (var view in Forecasts.UpcomingBills(services.Context.Current.Data, today).OrderBy(v => v.Upcoming.FirstOrDefault()?.Date ?? DateOnly.MaxValue))
            {
                Rows.Add(Line(view));
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static BillLine Line(BillView view)
    {
        var bill = view.Bill;
        var next = view.Upcoming.FirstOrDefault();
        var due = next switch
        {
            null => $"Not due in the next {Forecasts.DefaultDays} days",
            { Payment: { } paid } => $"Due {next.Date:ddd d MMM} · paid {paid.EffectiveDate:d MMM}"
                + (view.Upcoming.Count > 1 ? $" · then {view.Upcoming[1].Date:d MMM}" : ""),
            _ => $"Due {next.Date:ddd d MMM}",
        };
        var kind = bill.Purpose switch
        {
            BillPurpose.DebtPayment => "debt payment, not spending",
            BillPurpose.SavingsTransfer => "to savings",
            BillPurpose.Transfer => "transfer",
            _ => null,
        };
        var amount = bill.Amount.Kind == AmountKind.Estimated && bill.Amount.Low is { } low && bill.Amount.High is { } high
            ? $"{Describe.Money(bill.Amount.Expected!.Value)} ({Describe.Money(low)}–{Describe.Money(high)})"
            : bill.Amount.Expected is { } expected ? Describe.Money(expected) : "Unknown";
        var (status, tone) = next?.Payment is not null ? ("Paid", StatusTone.Healthy) : Describe.Confidence(bill.Provenance.Confidence);
        return new BillLine(view, bill.Name, kind is null ? due : $"{due} · {kind}", Describe.Schedule(bill.DueDates), amount, status, tone);
    }
}
