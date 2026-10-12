using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Import.Transactions;

namespace Mossmint.Desktop.ViewModels;

public sealed record ColumnOption(int? Index, string Label);

public sealed record AmountStyleOption(AmountStyle Style, string Label);

public sealed record DateFormatOption(string Format, string Label);

public sealed record KindOption(AccountKind Kind, string Label);

// Where a source account's rows go: an existing account, a new one, or nowhere.
public sealed record DestinationOption(Account? Account, string Label, bool IsNew = false);

public sealed partial class DestinationRow : ObservableObject
{
    public static readonly IReadOnlyList<KindOption> Kinds =
    [
        new(AccountKind.Checking, "Checking"), new(AccountKind.Savings, "Savings"), new(AccountKind.CreditCard, "Credit card"), new(AccountKind.Cash, "Cash"),
    ];

    public DestinationRow(string source, string label, IReadOnlyList<DestinationOption> choices, DestinationOption? selected, string newName,
        AccountKind kind = AccountKind.Checking)
    {
        Source = source;
        Label = label;
        Choices = choices;
        _selected = selected;
        _newName = newName;
        _newKind = Kinds.FirstOrDefault(k => k.Kind == kind) ?? Kinds[0];
    }

    // What the file calls this account: the value in a CSV's Account column
    // ("" when the whole file is one account), or a statement's account key.
    public string Source { get; }

    public string Label { get; }

    public IReadOnlyList<DestinationOption> Choices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    private DestinationOption? _selected;

    [ObservableProperty]
    private string _newName;

    [ObservableProperty]
    private KindOption _newKind;

    public bool IsNew => Selected?.IsNew == true;

    public static DestinationOption[] ChoicesFor(IReadOnlyList<Account> accounts, bool canLeaveOut) =>
    [
        .. accounts.Select(a => new DestinationOption(a, a.Name)),
        new(null, "A new account", IsNew: true),
        .. canLeaveOut ? [new DestinationOption(null, "Leave these rows out")] : Array.Empty<DestinationOption>(),
    ];

    // The account each row's source goes to, or null and an error when a choice
    // is missing. New accounts get their ID here so later files find them.
    public static IReadOnlyDictionary<string, Account>? Resolve(IEnumerable<DestinationRow> rows, Currency currency, Func<Guid> newId, out string? error)
    {
        error = null;
        var targets = new Dictionary<string, Account>();
        foreach (var row in rows)
        {
            if (row.Selected is null)
            {
                error = $"Choose an account for {row.Label}.";
                return null;
            }

            if (row.IsNew && string.IsNullOrWhiteSpace(row.NewName))
            {
                error = $"Give the new account for {row.Label} a name.";
                return null;
            }

            if (row.IsNew)
            {
                targets[row.Source] = new Account(new AccountId(newId()), row.NewName.Trim(), row.NewKind.Kind, currency);
            }
            else if (row.Selected.Account is { } account)
            {
                targets[row.Source] = account;
            }
        }

        if (targets.Count == 0)
        {
            error = "Every account is left out, so there's nothing to import.";
            return null;
        }

        return targets;
    }
}

// The step for an OFX or QFX file: its statements name their accounts, so the
// only question is which Mossmint account each one goes to.
public sealed partial class StatementFileViewModel : ObservableObject
{
    private readonly Currency _currency;

    public StatementFileViewModel(SourceFile file, string path, OfxFile ofx, Func<string, Guid?> remembered, IReadOnlyList<Account> accounts, Currency currency)
    {
        File = file;
        Path = path;
        Ofx = ofx;
        _currency = currency;
        Summary = ofx.Statements.Count == 1 ? "One statement." : $"{ofx.Statements.Count} statements.";
        foreach (var statement in ofx.Statements)
        {
            var key = OfxImport.AccountKey(statement);
            var choices = DestinationRow.ChoicesFor(accounts, canLeaveOut: ofx.Statements.Count > 1);
            var selected = remembered(key) is { } id ? choices.FirstOrDefault(c => c.Account?.Id.Value == id) : null;
            var balance = statement.Ledger is { } l ? $", balance as of {l.AsOf.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}" : "";
            var kind = statement.Type switch
            {
                OfxAccountType.CreditCard or OfxAccountType.CreditLine => AccountKind.CreditCard,
                OfxAccountType.Savings or OfxAccountType.MoneyMarket => AccountKind.Savings,
                _ => AccountKind.Checking,
            };
            Destinations.Add(new DestinationRow(key, $"{OfxImport.Describe(statement)} ({(statement.Transactions.Count == 1 ? "1 transaction" : $"{statement.Transactions.Count} transactions")}{balance})", choices, selected,
                OfxImport.Describe(statement), kind));
        }
    }

    public SourceFile File { get; }

    public string Path { get; }

    public OfxFile Ofx { get; }

    public string Summary { get; }

    public ObservableCollection<DestinationRow> Destinations { get; } = [];

    [ObservableProperty]
    private string? _error;

    public IReadOnlyDictionary<string, Account>? Targets(Func<Guid> newId)
    {
        if (Destinations.Count == 0)
        {
            Error = "This file has no bank or card statements in it.";
            return null;
        }

        var targets = DestinationRow.Resolve(Destinations, _currency, newId, out var error);
        Error = error;
        return targets;
    }
}

// The "Match the columns" step for one bank file: a guess (or the mapping
// confirmed last time) the user checks before the dry run. Every choice shows
// an example from the file, so a wrong column is easy to spot.
public sealed partial class BankFileViewModel : ObservableObject
{
    private const int MaxAccounts = 20;
    private readonly CsvTable _table;
    private readonly CsvMapping _guessed;
    private readonly IReadOnlyList<Account> _accounts;
    private readonly IReadOnlyDictionary<string, Guid> _remembered;
    private readonly Currency _currency;

    public BankFileViewModel(SourceFile file, string path, CsvTable table, CsvGuess guess, SavedCsvMapping? saved, IReadOnlyList<Account> accounts, Currency currency)
    {
        File = file;
        Path = path;
        _table = table;
        _accounts = accounts;
        _currency = currency;
        _guessed = saved?.Mapping ?? guess.Mapping;
        _remembered = saved?.Destinations ?? new Dictionary<string, Guid>();
        Fingerprint = CsvMappings.Fingerprint(table);
        Columns = [.. table.Header.Select((h, i) => new ColumnOption(i, Example(h, i)))];
        OptionalColumns = [new ColumnOption(null, "None"), .. Columns];
        Styles =
        [
            new(AmountStyle.SpendingNegative, "One column: spending is negative"),
            new(AmountStyle.SpendingPositive, "One column: spending is positive"),
            new(AmountStyle.SeparateColumns, "Separate columns for money out and money in"),
            new(AmountStyle.WithDirection, "Amounts with a debit or credit column"),
            .. _guessed.Amounts == AmountStyle.AsMossmint ? [new AmountStyleOption(AmountStyle.AsMossmint, "As Mossmint exported them")] : Array.Empty<AmountStyleOption>(),
        ];

        Summary = string.Join(" ", new[]
        {
            $"{table.Encoding switch { CsvEncoding.Utf16 => "UTF-16", CsvEncoding.Windows1252 => "Windows-1252", _ => "UTF-8" }} text, {Delimiter(table.Delimiter)}, {table.Rows.Count} rows.",
            table.SkippedLines switch { 0 => null, 1 => "1 line above the header was skipped.", var n => $"{n} lines above the header were skipped." },
            saved is not null ? "Matched the way you did last time." : guess.Preset is { } preset ? $"This looks like an export from {preset}." : null,
        }.OfType<string>());
        EncodingWarning = table.Encoding == CsvEncoding.Windows1252
            ? "This file isn't UTF-8, so Mossmint read it as Windows-1252. If accented names look wrong in the examples, save the file as UTF-8 and choose it again."
            : null;

        _dateColumn = Column(_guessed.Date);
        _descriptionColumn = Column(_guessed.Description);
        _style = Styles.FirstOrDefault(s => s.Style == _guessed.Amounts) ?? Styles[0];
        _amountColumn = Column(_guessed.Amount);
        _moneyOutColumn = Column(_guessed.MoneyOut);
        _moneyInColumn = Column(_guessed.MoneyIn);
        _directionColumn = Column(_guessed.Direction);
        _accountColumn = _guessed.Account is { } a ? Column(a) : OptionalColumns[0];
        RefreshDateFormats(_guessed.DateFormat);
        RefreshDestinations();
    }

    public SourceFile File { get; }

    public string Path { get; }

    public string Fingerprint { get; }

    public string Summary { get; }

    public string? EncodingWarning { get; }

    public IReadOnlyList<ColumnOption> Columns { get; }

    public IReadOnlyList<ColumnOption> OptionalColumns { get; }

    public IReadOnlyList<AmountStyleOption> Styles { get; }

    public ObservableCollection<DateFormatOption> DateFormats { get; } = [];

    public ObservableCollection<DestinationRow> Destinations { get; } = [];

    [ObservableProperty]
    private ColumnOption _dateColumn;

    [ObservableProperty]
    private DateFormatOption? _dateFormat;

    [ObservableProperty]
    private bool _isDateAmbiguous;

    [ObservableProperty]
    private ColumnOption _descriptionColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesOneAmount), nameof(UsesSeparateColumns), nameof(UsesDirection))]
    private AmountStyleOption _style;

    [ObservableProperty]
    private ColumnOption _amountColumn;

    [ObservableProperty]
    private ColumnOption _moneyOutColumn;

    [ObservableProperty]
    private ColumnOption _moneyInColumn;

    [ObservableProperty]
    private ColumnOption _directionColumn;

    [ObservableProperty]
    private ColumnOption _accountColumn;

    [ObservableProperty]
    private string? _error;

    public bool UsesOneAmount => Style.Style is AmountStyle.SpendingNegative or AmountStyle.SpendingPositive or AmountStyle.AsMossmint or AmountStyle.WithDirection;

    public bool UsesSeparateColumns => Style.Style == AmountStyle.SeparateColumns;

    public bool UsesDirection => Style.Style == AmountStyle.WithDirection;

    partial void OnDateColumnChanged(ColumnOption value) => RefreshDateFormats(DateFormat?.Format);

    partial void OnAccountColumnChanged(ColumnOption value) => RefreshDestinations();

    public CsvMapping Mapping() => _guessed with
    {
        Date = DateColumn.Index ?? 0,
        DateFormat = DateFormat?.Format ?? "",
        Description = DescriptionColumn.Index ?? 0,
        Amounts = Style.Style,
        Amount = UsesOneAmount ? AmountColumn.Index : null,
        MoneyOut = UsesSeparateColumns ? MoneyOutColumn.Index : null,
        MoneyIn = UsesSeparateColumns ? MoneyInColumn.Index : null,
        Direction = UsesDirection ? DirectionColumn.Index : null,
        Account = AccountColumn.Index,
    };

    // The accounts each source account goes to, or null with Error set when a
    // choice is missing. New accounts get their ID here so later files find them.
    public IReadOnlyDictionary<string, Account>? Targets(Func<Guid> newId)
    {
        Error = null;
        if (DateFormat is null)
        {
            Error = "None of the date formats Mossmint knows reads every date in that column. Choose the column that holds the dates.";
            return null;
        }

        if (Destinations.Count == 0)
        {
            Error = $"That column has more than {MaxAccounts} different values. Choose the column that names the account, or None.";
            return null;
        }

        var targets = DestinationRow.Resolve(Destinations, _currency, newId, out var error);
        Error = error;
        return targets;
    }

    public static SavedCsvMapping Remember(CsvMapping mapping, IReadOnlyDictionary<string, Account> targets) =>
        new(mapping, targets.ToDictionary(t => t.Key, t => t.Value.Id.Value));

    private void RefreshDateFormats(string? keep)
    {
        var values = Values(DateColumn.Index).Where(CsvValues.CouldBeDate).ToList();
        var fitting = CsvMappings.DateFormatChoices.Where(f => values.Count > 0 && values.All(v => CsvValues.TryDate(v, f, out _))).ToList();
        DateFormats.Clear();
        foreach (var format in fitting)
        {
            CsvValues.TryDate(values[0], format, out var date);
            DateFormats.Add(new DateFormatOption(format, $"{format} · {values[0]} is {date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"));
        }

        DateFormat = DateFormats.FirstOrDefault(f => f.Format == keep) ?? DateFormats.FirstOrDefault();
        IsDateAmbiguous = DateFormats.Count > 1;
    }

    private void RefreshDestinations()
    {
        Destinations.Clear();
        var sources = AccountColumn.Index is { } column
            ? _table.Rows.Select(r => column < r.Fields.Count ? r.Fields[column].Trim() : "").GroupBy(v => v).Select(g => (g.Key, g.Count())).ToList()
            : [("", _table.Rows.Count)];
        if (sources.Count > MaxAccounts)
        {
            return;
        }

        foreach (var (source, count) in sources)
        {
            var choices = DestinationRow.ChoicesFor(_accounts, canLeaveOut: AccountColumn.Index is not null);
            var selected = _remembered.TryGetValue(source, out var id) ? choices.FirstOrDefault(c => c.Account?.Id.Value == id) : null;
            selected ??= source.Length > 0 ? choices.FirstOrDefault(c => string.Equals(c.Account?.Name, source, StringComparison.CurrentCultureIgnoreCase)) : null;
            var label = source.Length == 0 ? $"this file ({count} rows)" : $"'{source}' ({count} rows)";
            Destinations.Add(new DestinationRow(source, label, choices, selected, source.Length > 0 ? source : ""));
        }
    }

    private ColumnOption Column(int? index) => Columns.FirstOrDefault(c => c.Index == index) ?? Columns[0];

    private IEnumerable<string> Values(int? column) =>
        column is { } c ? _table.Rows.Select(r => c < r.Fields.Count ? r.Fields[c].Trim() : "").Where(v => v.Length > 0) : [];

    private string Example(string header, int column) =>
        Values(column).FirstOrDefault() is { } value ? $"{header} · {(value.Length > 28 ? value[..27] + "…" : value)}" : header;

    private static string Delimiter(char c) => c switch
    {
        ',' => "comma-separated",
        ';' => "semicolon-separated",
        '\t' => "tab-separated",
        _ => "separated by |",
    };
}
