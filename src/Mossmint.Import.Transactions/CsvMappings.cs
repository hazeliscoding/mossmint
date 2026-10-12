using System.Security.Cryptography;
using System.Text;

namespace Mossmint.Import.Transactions;

// A first guess at which column holds what. The user sees it and can change
// it before anything is imported. DateFormats lists every format that reads
// all the dates: more than one means the file doesn't say whether 01/02 is
// January or February, and the user has to choose.
public sealed record CsvGuess(CsvMapping Mapping, IReadOnlyList<string> DateFormats, string? Preset);

public static class CsvMappings
{
    // Month-first before day-first: an ambiguous file is shown with the US
    // reading selected and the choice flagged.
    public static readonly IReadOnlyList<string> DateFormatChoices =
    [
        "yyyy-MM-dd", "M/d/yyyy", "d/M/yyyy", "M/d/yy", "d/M/yy", "yyyy/M/d", "d.M.yyyy", "d.M.yy", "M-d-yyyy", "d-M-yyyy",
        "yyyyMMdd", "MMM d, yyyy", "d MMM yyyy", "d-MMM-yyyy", "d-MMM-yy",
    ];

    private static readonly string[] DescriptionNames = ["description", "payee", "merchant", "name", "transaction description", "details", "memo", "narrative"];
    private static readonly string[] OutNames = ["debit", "debits", "withdrawal", "withdrawals", "outflow", "money out", "paid out", "debit amount"];
    private static readonly string[] InNames = ["credit", "credits", "deposit", "deposits", "inflow", "money in", "paid in", "credit amount"];

    // Exports from other apps, recognized by their column names so a reordered
    // or extended header still matches. Mossmint's own export comes first.
    private static readonly (string Name, string[] Columns, Func<Func<string, int>, CsvMapping> Build)[] Presets =
    [
        ("Mossmint", ["date", "posted", "account", "description", "category", "amount", "currency", "status", "source"],
            c => new CsvMapping(c("date"), "", c("description"), AmountStyle.AsMossmint, Amount: c("amount"), Account: c("account"), Status: c("status"), UnescapeFormulas: true)),
        ("YNAB", ["account", "date", "payee", "outflow", "inflow"],
            c => new CsvMapping(c("date"), "", c("payee"), AmountStyle.SeparateColumns, MoneyOut: c("outflow"), MoneyIn: c("inflow"), Account: c("account"))),
        ("Mint", ["date", "description", "amount", "transaction type", "account name"],
            c => new CsvMapping(c("date"), "", c("description"), AmountStyle.WithDirection, Amount: c("amount"), Direction: c("transaction type"), Account: c("account name"))),
        ("Monarch", ["date", "merchant", "account", "amount"],
            c => new CsvMapping(c("date"), "", c("merchant"), AmountStyle.SpendingNegative, Amount: c("amount"), Account: c("account"))),
        ("Actual", ["account", "date", "payee", "amount"],
            c => new CsvMapping(c("date"), "", c("payee"), AmountStyle.SpendingNegative, Amount: c("amount"), Account: c("account"))),
    ];

    // Saved mappings are found again by the header row, whatever the rows hold.
    public static string Fingerprint(CsvTable table) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(table.HasHeader
            ? string.Join('\n', table.Header.Select(h => h.Trim().ToLowerInvariant()))
            : $"no header\n{table.Header.Count}")))[..32];

    public static CsvGuess Guess(CsvTable table)
    {
        var names = table.HasHeader ? table.Header.Select(h => h.Trim().ToLowerInvariant()).ToList() : [];
        int Named(string name) => names.IndexOf(name);
        IEnumerable<string> Values(int column) => table.Rows.Select(r => column < r.Fields.Count ? r.Fields[column].Trim() : "").Where(v => v.Length > 0);

        var preset = Presets.FirstOrDefault(p => p.Columns.All(names.Contains));
        var columns = Enumerable.Range(0, table.Header.Count).ToList();
        var formats = columns.ToDictionary(c => c, c => Values(c).Where(CsvValues.CouldBeDate).ToList() is { Count: > 0 } dates
            ? DateFormatChoices.Where(f => dates.All(v => CsvValues.TryDate(v, f, out _))).ToList()
            : []);
        var date = preset.Name is null
            ? columns.Where(c => formats[c].Count > 0).OrderBy(c => names.ElementAtOrDefault(c)?.Contains("date", StringComparison.Ordinal) == true ? 0 : 1).DefaultIfEmpty(0).First()
            : preset.Build(Named).Date;

        var decimalComma = columns.Where(c => c != date).SelectMany(Values).Count(v => v.Length > 3 && v[^3] == ',' && char.IsDigit(v[^1]))
            > columns.Where(c => c != date).SelectMany(Values).Count(v => v.Length > 3 && v[^3] == '.' && char.IsDigit(v[^1]));
        var separator = decimalComma ? ',' : '.';
        var numeric = columns.Where(c => c != date && Values(c).Any() && Values(c).All(v => CsvValues.TryAmount(v, separator, out _))).ToList();

        CsvMapping mapping;
        if (preset.Name is not null)
        {
            mapping = preset.Build(Named);
        }
        else
        {
            var description = DescriptionNames.Select(Named).FirstOrDefault(c => c >= 0, -1) is var named and >= 0
                ? named
                : columns.Where(c => c != date && !numeric.Contains(c)).OrderByDescending(c => Values(c).Select(v => v.Length).DefaultIfEmpty(0).Average()).DefaultIfEmpty(0).First();
            var moneyOut = OutNames.Select(Named).FirstOrDefault(c => c >= 0, -1);
            var moneyIn = InNames.Select(Named).FirstOrDefault(c => c >= 0, -1);
            var amount = names.FindIndex(n => n.Contains("amount", StringComparison.Ordinal) && !OutNames.Contains(n) && !InNames.Contains(n));
            mapping = amount < 0 && moneyOut >= 0 && moneyIn >= 0
                ? new CsvMapping(date, "", description, AmountStyle.SeparateColumns, MoneyOut: moneyOut, MoneyIn: moneyIn)
                : new CsvMapping(date, "", description, AmountStyle.SpendingNegative, Amount: amount >= 0 ? amount : numeric.FirstOrDefault(c => c != description));
        }

        var dateFormats = formats.GetValueOrDefault(mapping.Date) ?? [];
        return new CsvGuess(mapping with { DateFormat = dateFormats.FirstOrDefault() ?? DateFormatChoices[0], DecimalSeparator = separator }, dateFormats, preset.Name);
    }
}
