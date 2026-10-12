using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Import.Transactions;

// How a file shows money leaving the account. Mossmint itself signs amounts
// the way the account reports them (a card charge is positive), so every style
// but AsMossmint is turned around for cards and loans.
public enum AmountStyle
{
    SpendingNegative,
    SpendingPositive,

    // Money out and money in sit in separate columns.
    SeparateColumns,

    // Amounts are all positive; another column says debit or credit.
    WithDirection,

    // Mossmint's own transactions.csv.
    AsMossmint,
}

// Which column holds what, by position. Null means the file has no such column.
// Without an Account column, every row goes to the destination keyed "".
public sealed record CsvMapping(
    int Date,
    string DateFormat,
    int Description,
    AmountStyle Amounts,
    int? Amount = null,
    int? MoneyOut = null,
    int? MoneyIn = null,
    int? Direction = null,
    int? Account = null,
    int? Status = null,
    char DecimalSeparator = '.',
    bool UnescapeFormulas = false);

// Turns a read CSV file into an import batch for the accounts the user chose.
// Rows that don't look like transactions are left out with a note; a
// transaction Mossmint can't read blocks the import instead of being guessed.
public static partial class CsvImport
{
    public const string Mapper = "csv";
    private const int Listed = 5;

    public static ImportBatch Map(SourceFile file, CsvTable table, CsvMapping mapping, IReadOnlyDictionary<string, Account> targets, DateOnly today, Currency currency)
    {
        var findings = new List<ImportFinding>();
        var transactions = new List<ProposedTransaction>();
        var used = new List<Account>();
        var occurrences = new Dictionary<string, int>();
        var undated = new List<SourceRef>();
        var voided = new List<SourceRef>();
        var unplaced = new Dictionary<string, int>();
        var placed = 0;
        foreach (var row in table.Rows)
        {
            string Cell(int? column) => column is { } c && c < row.Fields.Count ? Clean(row.Fields[c], mapping.UnescapeFormulas) : "";
            var source = new SourceRef(file.Name, $"line {row.Line}");
            var accountName = Cell(mapping.Account);
            if (!targets.TryGetValue(accountName, out var account))
            {
                unplaced[accountName] = unplaced.GetValueOrDefault(accountName) + 1;
                continue;
            }

            placed++;
            if (!CsvValues.TryDate(Cell(mapping.Date), mapping.DateFormat, out var date))
            {
                undated.Add(source);
                continue;
            }

            if (Cell(mapping.Status).Equals("void", StringComparison.OrdinalIgnoreCase))
            {
                voided.Add(source);
                continue;
            }

            if (Value(Cell, mapping) is not { } value)
            {
                findings.Add(Finding("csv-amount", FindingSeverity.Blocking, $"Line {row.Line}: the amount can't be read",
                    $"'{AmountText(Cell, mapping)}' isn't an amount Mossmint can read. Fix the file or change which columns hold the amount.", [source]));
                continue;
            }

            var amount = new Money(mapping.Amounts == AmountStyle.AsMossmint || !account.IsLiability ? value : -value, currency);
            if (!amount.IsWholeMinorUnits)
            {
                findings.Add(Finding("csv-fractional-amount", FindingSeverity.Blocking, $"Line {row.Line}: an amount has fractions of a cent",
                    $"{amount.Amount.ToString(CultureInfo.InvariantCulture)} can't be stored exactly.", [source]));
                continue;
            }

            var description = Cell(mapping.Description);
            var identity = $"{date:yyyy-MM-dd}\n{amount.Amount.ToString("F" + currency.MinorUnits, CultureInfo.InvariantCulture)}\n{Spaces().Replace(description, " ")}";
            var occurrence = occurrences[$"{account.Id.Value}\n{identity}"] = occurrences.GetValueOrDefault($"{account.Id.Value}\n{identity}") + 1;
            if (!used.Contains(account))
            {
                used.Add(account);
            }

            transactions.Add(new ProposedTransaction(ImportKeys.Account(account.Id), $"csv:{Hash($"{identity}\n{occurrence}")}", date, amount,
                Cell(mapping.Status).Equals("pending", StringComparison.OrdinalIgnoreCase) ? TransactionStatus.Pending : TransactionStatus.Posted,
                description, source));
        }

        if (undated.Count * 2 > placed)
        {
            findings.Add(Finding("csv-date-format", FindingSeverity.Blocking, "Most dates don't match the date format",
                $"{undated.Count} of {placed} rows have no date in the {mapping.DateFormat} format. Choose the format the file uses.", [.. undated.Take(Listed)]));
        }
        else if (undated.Count > 0)
        {
            findings.Add(Finding("csv-row-skipped", FindingSeverity.Info, $"{Rows(undated.Count)} without a date left out",
                "They don't start with a date, so they look like totals or notes rather than transactions.", [.. undated.Take(Listed)]));
        }

        if (voided.Count > 0)
        {
            findings.Add(Finding("csv-void-skipped", FindingSeverity.Info, $"{Rows(voided.Count)} marked void left out",
                "Void entries were undone where they came from, so they aren't imported.", [.. voided.Take(Listed)]));
        }

        foreach (var (name, count) in unplaced)
        {
            findings.Add(Finding("csv-account-skipped", FindingSeverity.Info, $"Rows for '{name}' left out",
                $"{Rows(count).ToLowerInvariant()} for '{name}' have no Mossmint account chosen, so they weren't imported.", [new SourceRef(file.Name, "")]));
        }

        return new ImportBatch(Mapper, ImportOrigin.BankFile, today, currency, [new SourceFileInfo(file.Name, file.Sha256, file.Content.Length, "Bank CSV")])
        {
            Accounts = [.. used.Select(a => new ProposedAccount(ImportKeys.Account(a.Id), a.Name, a.Kind, [new SourceRef(file.Name, "")]))],
            Transactions = transactions,
            Findings = findings,
        };
    }

    // Money in is positive, from the account holder's side.
    private static decimal? Value(Func<int?, string> cell, CsvMapping mapping)
    {
        decimal? Read(int? column, bool blankIsZero) =>
            cell(column) is var text && blankIsZero && text.Length == 0 ? 0m
            : CsvValues.TryAmount(text, mapping.DecimalSeparator, out var amount) ? amount : null;

        // Where a column already says which way the money went, a negative
        // number could be money the other way or a reversal, so it isn't read.
        decimal? Unsigned(int? column, bool blankIsZero) => Read(column, blankIsZero) is { } value && value >= 0 ? value : null;

        switch (mapping.Amounts)
        {
            case AmountStyle.SpendingNegative or AmountStyle.AsMossmint:
                return Read(mapping.Amount, false);
            case AmountStyle.SpendingPositive:
                return -Read(mapping.Amount, false);
            case AmountStyle.SeparateColumns:
                return Unsigned(mapping.MoneyIn, true) - Unsigned(mapping.MoneyOut, true);
            default:
                var direction = cell(mapping.Direction).ToLowerInvariant();
                var sign = direction is "credit" or "cr" or "deposit" ? 1 : direction is "debit" or "dr" or "withdrawal" ? -1 : 0;
                return sign == 0 ? null : sign * Unsigned(mapping.Amount, false);
        }
    }

    private static string AmountText(Func<int?, string> cell, CsvMapping mapping) => mapping.Amounts switch
    {
        AmountStyle.SeparateColumns => $"{cell(mapping.MoneyOut)} / {cell(mapping.MoneyIn)}",
        AmountStyle.WithDirection => $"{cell(mapping.Amount)} {cell(mapping.Direction)}",
        _ => cell(mapping.Amount),
    };

    // Mossmint's export puts an apostrophe before text a spreadsheet would run
    // as a formula (docs/operations/backup-and-restore.md); reading it back drops it.
    private static string Clean(string raw, bool unescape) =>
        (unescape && raw.Length > 1 && raw[0] == '\'' && raw[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? raw[1..] : raw).Trim();

    private static string Rows(int count) => count == 1 ? "1 row" : $"{count} rows";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private static ImportFinding Finding(string code, FindingSeverity severity, string title, string detail, IReadOnlyList<SourceRef> sources) =>
        new(code, severity, title, detail, sources);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
