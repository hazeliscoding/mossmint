using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Desktop.Controls;
using Mossmint.Desktop.Services;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Desktop.ViewModels;

// A ledger line: an entry, or a balance the bank showed, so every change to the
// opening cash can be traced. Entry is null for balance lines.
public sealed record LedgerRow(string Date, string Description, string Detail, string Source, string Amount, string Status, StatusTone Tone, Transaction? Entry)
{
    public bool HasDetail => Detail.Length > 0;

    public bool CanVoid => Entry is { Status: not TransactionStatus.Void, Provenance.Source: SourceKind.UserConfirmed };

    public string VoidName => $"Void {Description} on {Date}";
}

public sealed partial class TransactionsPageViewModel(AppServices services, IAppNavigator navigator)
    : PageViewModel(Destination.Transactions, "Transactions", "What went in and out by hand, and the balances your bank showed.")
{
    // The empty page's way in: that one setup step, then back here.
    [RelayCommand]
    private void AddAccount() => navigator.StartSetup(SetupStep.Cash, Destination.Transactions);

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public ObservableCollection<Account> Accounts { get; } = [];

    public ObservableCollection<LedgerRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty]
    private Account? _account;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string? _formError;

    // Add an entry
    [ObservableProperty]
    private bool _isAdding;

    [ObservableProperty]
    private string _entryDescription = "";

    [ObservableProperty]
    private string _entryAmount = "";

    [ObservableProperty]
    private bool _isMoneyIn;

    [ObservableProperty]
    private DateTime? _entryDate;

    [ObservableProperty]
    private string _entryCategory = "";

    // Reconcile
    [ObservableProperty]
    private bool _isReconciling;

    [ObservableProperty]
    private string _bankBalance = "";

    [ObservableProperty]
    private string _expectedText = "";

    private DateOnly Today => DateOnly.FromDateTime(services.Clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo()
    {
        Notice = null;
        CancelForms();
        Accounts.Clear();
        if (!services.Context.IsUnlocked)
        {
            Account = null;
            Load();
            return;
        }

        var data = services.Context.Current.Data;
        foreach (var account in data.Accounts().Where(a => a.Kind is AccountKind.Checking or AccountKind.Savings or AccountKind.Cash))
        {
            Accounts.Add(account);
        }

        var keep = Account?.Id ?? Forecasts.SpendingAccount(data.Accounts(), data.Income())?.Id;
        var next = Accounts.FirstOrDefault(a => a.Id == keep) ?? Accounts.FirstOrDefault();
        if (next == Account)
        {
            Load();
        }

        Account = next;
    }

    partial void OnAccountChanged(Account? value)
    {
        CancelForms();
        Load();
    }

    [RelayCommand]
    private void StartAdd()
    {
        CancelForms();
        (EntryDescription, EntryAmount, IsMoneyIn, EntryCategory) = ("", "", false, "");
        EntryDate = Today.ToDateTime(TimeOnly.MinValue);
        IsAdding = true;
    }

    [RelayCommand]
    private void StartReconcile()
    {
        CancelForms();
        BankBalance = "";
        ExpectedText = Entry().Expected(Account!.Id) is { } expected
            ? $"Mossmint expects {Describe.Money(expected)}: the last balance plus the entries since. Enter what your bank shows now."
            : "There's no balance yet. Enter what your bank shows now.";
        IsReconciling = true;
    }

    [RelayCommand]
    private void CancelForms()
    {
        (IsAdding, IsReconciling, FormError) = (false, false, null);
    }

    [RelayCommand]
    private void SaveEntry() => Save(() =>
    {
        var amount = MoneyInput.Required(EntryAmount, _culture, "Amount");
        var date = EntryDate is { } d ? DateOnly.FromDateTime(d) : Today;
        var entry = Entry().RecordEntry(Account!.Id, EntryDescription, IsMoneyIn ? Math.Abs(amount) : -Math.Abs(amount), date, EntryCategory);
        var latest = services.Context.Current.Data.Balances(Account.Id).FirstOrDefault();
        Notice = latest is not null && !Forecasts.IsSinceBalance(latest, entry)
            ? $"{entry.Description} recorded. It's dated before the {latest.Provenance.AsOf:d MMM} balance, which already includes it, so today's cash doesn't change."
            : $"{entry.Description} recorded. {Account.Name} is now expected to hold {Describe.Money(Entry().Expected(Account.Id) ?? entry.Amount)}.";
    });

    [RelayCommand]
    private void SaveReconcile() => Save(() =>
    {
        var (balance, difference) = Entry().Reconcile(Account!.Id, MoneyInput.Required(BankBalance, _culture, "Balance your bank shows"));
        Notice = difference switch
        {
            null => $"{Account.Name}: {Describe.Money(balance.Balance)} confirmed today.",
            { IsZero: true } => $"{Account.Name}: {Describe.Money(balance.Balance)} matches what Mossmint expected. Reconciled.",
            { } d => $"{Account.Name}: the bank shows {Describe.Money(balance.Balance)}, {Describe.Money(d.Abs())} {(d.IsNegative ? "less" : "more")} than Mossmint expected. "
                + "The difference is kept on this balance for review, and the forecast now starts from the bank's figure.",
        };
    });

    [RelayCommand]
    private void Void(LedgerRow row)
    {
        Entry().Undo(row.Entry!);
        Notice = $"{row.Description} on {row.Date} is void. It no longer counts.";
        Load();
    }

    private void Save(Action save)
    {
        try
        {
            save();
            CancelForms();
            Load();
        }
        catch (ArgumentException e)
        {
            FormError = e.Message;
        }
    }

    private ManualEntry Entry() => new(services.Context.Current.Data, services.Clock);

    private void Load()
    {
        Rows.Clear();
        Summary = "";
        if (Account is { } account && services.Context.IsUnlocked)
        {
            var data = services.Context.Current.Data;
            var balances = data.Balances(account.Id);
            var latest = balances.FirstOrDefault();
            var entries = data.Transactions(account.Id);
            var bills = data.Bills().ToDictionary(b => b.Id, b => b.Name);
            var lines = entries.Select(e => (e.EffectiveDate, e.RecordedAt, Row: Row(e, latest, bills)))
                .Concat(balances.Select(b => (b.Provenance.AsOf, b.RecordedAt, Row: Row(b))))
                .OrderByDescending(l => l.Item1).ThenByDescending(l => l.RecordedAt ?? DateTimeOffset.MinValue);
            foreach (var line in lines)
            {
                Rows.Add(line.Row);
            }

            var since = latest is null ? 0 : entries.Count(e => Forecasts.IsSinceBalance(latest, e));
            Summary = latest is null
                ? $"{account.Name} · no balance yet"
                : $"{account.Name} · {Describe.Money(latest.Balance)} on {latest.Provenance.AsOf:d MMM} · "
                    + (since == 0 ? "nothing entered since" : $"now {Describe.Money(Entry().Expected(account.Id)!.Value)} after {(since == 1 ? "1 entry" : $"{since} entries")}");
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static LedgerRow Row(Transaction e, BalanceSnapshot? latest, Dictionary<Domain.Planning.BillId, string> bills)
    {
        var (status, tone) = e.Status == TransactionStatus.Void ? ("Void", StatusTone.Neutral)
            : e.Provenance.Source != SourceKind.UserConfirmed ? Describe.Confidence(e.Provenance.Confidence)
            : latest is not null && !Forecasts.IsSinceBalance(latest, e) ? ("In balance", StatusTone.Healthy)
            : ("Since balance", StatusTone.Info);
        var detail = string.Join(" · ", new[]
        {
            e.Pays is { } paid ? $"pays {bills.GetValueOrDefault(paid.Bill, "a bill")} due {paid.Nominal:d MMM}" : null,
            e.Category,
        }.OfType<string>());
        var source = e.Pays is not null ? "Mark paid" : e.Provenance.Source switch
        {
            SourceKind.UserConfirmed => "Manual",
            SourceKind.Connector => "Bank feed",
            _ => "Imported",
        };
        return new LedgerRow(e.EffectiveDate.ToString("ddd d MMM", CultureInfo.CurrentCulture), e.Description, detail, source,
            e.Amount.IsNegative ? Describe.Money(e.Amount) : "+" + Describe.Money(e.Amount), status, tone, e);
    }

    private static LedgerRow Row(BalanceSnapshot b)
    {
        var (status, tone) = b.State switch
        {
            ReconciliationState.Reconciled => ("Reconciled", StatusTone.Healthy),
            ReconciliationState.Discrepancy => ("Differs", StatusTone.Warning),
            _ => Describe.Confidence(b.Provenance.Confidence),
        };
        return new LedgerRow(b.Provenance.AsOf.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            b.Provenance.Source == SourceKind.UserConfirmed ? "Balance confirmed" : "Balance recorded",
            "what the account held that day; entries before it are included", "Balance", Describe.Money(b.Balance), status, tone, null);
    }
}
