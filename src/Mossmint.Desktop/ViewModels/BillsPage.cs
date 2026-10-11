using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Desktop.ViewModels;

// A bill and its next due date in the forecast window, paid or not.
public sealed record BillLine(BillView View, string Name, string Detail, string Cadence, string Amount, string Status, StatusTone Tone)
{
    public BillDue? Next => View.Upcoming.FirstOrDefault();

    public bool CanMarkPaid => Next is { Payment: null } && View.Bill.Amount.Expected is not null;

    public bool CanUndo => Next?.Payment is not null;

    public string MarkPaidName => $"Mark {Name} paid";

    public string UndoName => $"Undo {Name} paid";

    public string EditName => $"Edit {Name}";
}

public sealed partial class BillsPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Bills, "Bills", "Recurring bills and payments. Mark one paid when the money leaves your account.")
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private RecurringBill? _editing;

    // That one setup step, then back here.
    [RelayCommand]
    private void AddBills() => navigator.StartSetup(SetupStep.Bills, Destination.Bills);

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editTitle = "";

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editAmount = "";

    [ObservableProperty]
    private bool _editIsEstimate;

    [ObservableProperty]
    private string _editLow = "";

    [ObservableProperty]
    private string _editHigh = "";

    [ObservableProperty]
    private decimal? _editDay = 1;

    [ObservableProperty]
    private bool _editLastDay;

    [ObservableProperty]
    private DateTime? _endFrom;

    [ObservableProperty]
    private string? _formError;

    // A debt's payment changes but stays while the debt is owed.
    [ObservableProperty]
    private bool _canEnd;

    [RelayCommand]
    private void StartEdit(BillLine line)
    {
        var bill = _editing = line.View.Bill;
        var day = bill.DueDates.Rule is MonthlyRule(var d) ? d : (DayOfMonth?)null;
        (EditTitle, EditName, EditIsEstimate, EditLastDay, EditDay) =
            ($"Change {bill.Name}", bill.Name, bill.Amount.Kind == AmountKind.Estimated, day?.IsLastDay ?? false, day is { IsLastDay: false } of ? of.Day : 1);
        (EditAmount, EditLow, EditHigh) = (Text(bill.Amount.Expected), Text(bill.Amount.Low), Text(bill.Amount.High));
        (EndFrom, CanEnd, FormError, IsEditing) = (services.Clock.GetLocalNow().Date, bill.Purpose != BillPurpose.DebtPayment, null, true);
    }

    [RelayCommand]
    private void SaveEdit() => Change(bill =>
    {
        var entry = new BillEntry(
            EditName,
            MoneyInput.Required(EditAmount, _culture, "Amount"),
            EditLastDay ? 0 : (int)(EditDay ?? 0),
            bill.Purpose,
            EditIsEstimate,
            EditIsEstimate ? MoneyInput.Optional(EditLow, _culture, "Lowest usual amount") : null,
            EditIsEstimate ? MoneyInput.Optional(EditHigh, _culture, "Highest usual amount") : null);
        var saved = Entry().UpdateBill(bill, entry);
        return $"{saved.Name} saved. The forecast uses it from now on.";
    });

    [RelayCommand]
    private void EndBill() => Change(bill =>
    {
        var from = DateOnly.FromDateTime(EndFrom ?? services.Clock.GetLocalNow().Date);
        Entry().EndBill(bill, from);
        return $"{bill.Name} ends: nothing is due from {from:d MMM yyyy}. Its paid history stays.";
    });

    [RelayCommand]
    private void RemoveBill() => Change(bill =>
    {
        Entry().RemoveBill(bill);
        return $"{bill.Name} removed.";
    });

    [RelayCommand]
    private void CancelEdit() => (IsEditing, FormError, _editing) = (false, null, null);

    // A refused change keeps the form open with the reason; nothing is saved.
    private void Change(Func<RecurringBill, string> change)
    {
        if (_editing is not { } bill)
        {
            return;
        }

        try
        {
            Notice = change(bill);
            CancelEdit();
            Load();
        }
        catch (ArgumentException e)
        {
            FormError = e.Message;
        }
    }

    private string Text(Domain.Money? amount) => amount?.Amount.ToString("N2", _culture) ?? "";

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
        CancelEdit();
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
            null when bill.DueDates.EndsOn is { } end => $"Ended: nothing due from {end.AddDays(1):d MMM yyyy}",
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
