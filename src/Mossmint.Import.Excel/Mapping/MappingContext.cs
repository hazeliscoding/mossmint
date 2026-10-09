using System.Globalization;
using System.Text;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Import.Excel.Xlsx;

namespace Mossmint.Import.Excel.Mapping;

// One workbook being mapped: its cells, where they came from, and what the
// mapping found. Values are converted strictly; anything ambiguous becomes a
// finding instead of a guess.
internal sealed class MappingContext(string fileName, XlsxWorkbook workbook, Currency currency, DateOnly today, ImportCollector output)
{
    public string FileName { get; } = fileName;

    public XlsxWorkbook Workbook { get; } = workbook;

    public Currency Currency { get; } = currency;

    public DateOnly Today { get; } = today;

    public ImportCollector Output { get; } = output;

    public SourceRef Ref(XlsxSheet sheet, XlsxCell cell) => Ref(sheet, cell.Address);

    public SourceRef Ref(XlsxSheet sheet, string address) =>
        new(FileName, $"{(sheet.Name.Any(c => !char.IsLetterOrDigit(c)) ? $"'{sheet.Name}'" : sheet.Name)}!{address}");

    // Spreadsheet numbers are binary floating point, so 1879.24 may be stored
    // as 1879.2400000000002. Noise below a millionth of a cent is dropped; a
    // genuine fraction of a cent is rounded and reported.
    public Money? Money(XlsxSheet sheet, XlsxCell? cell, string what)
    {
        if (cell?.Number is not { } value)
        {
            return null;
        }

        var rounded = decimal.Round(value, Currency.MinorUnits, MidpointRounding.AwayFromZero);
        if (Math.Abs(value - rounded) > 0.000001m)
        {
            Output.Review("fractional-amount", $"{what} has fractions of a cent",
                $"The cell holds {value.ToString(CultureInfo.InvariantCulture)}; Mossmint uses {ImportText.Amount(new Money(rounded, Currency))}.",
                [Ref(sheet, cell)]);
        }

        return new Money(rounded, Currency);
    }

    public DateOnly? Date(XlsxCell? cell)
    {
        switch (cell)
        {
            // Plain serial numbers count too: a date that lost its format is still a date.
            case { Number: { } serial } when cell.IsDateFormat || serial is >= 20000m and < 80000m:
                return Workbook.ToDate(serial);
            case { Text: { } text } when DateOnly.TryParse(text.Trim(), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    // Rates are stored as fractions (0.2212 for 22.12%). A whole-number rate was
    // typed as a percent; it is converted and noted.
    public AnnualRate? Rate(XlsxSheet sheet, XlsxCell? cell, string what)
    {
        if (cell?.Number is not { } value || value < 0m)
        {
            return null;
        }

        if (value >= 1m && value < 100m)
        {
            Output.Note("percent-rate", $"{what} read as a percent", $"{value.ToString(CultureInfo.InvariantCulture)} is taken as {value}%.", [Ref(sheet, cell)]);
            value /= 100m;
        }

        return value < 10m ? new AnnualRate(value) : null;
    }

    public static int? Whole(XlsxCell? cell) =>
        cell?.Number is { } n && n == decimal.Truncate(n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    // Flags a used value that only Google Sheets can recompute.
    public void CheckGoogleOnly(XlsxSheet sheet, XlsxCell? cell, string what)
    {
        if (cell is { IsGoogleOnly: true })
        {
            Output.Review("google-only-formula", $"{what} comes from a Google Sheets formula",
                "Excel can't recalculate this cell, so Mossmint used the value Google Sheets last saved. Check it against the original sheet.",
                [Ref(sheet, cell)]);
        }
    }

    public void Raw(string kind, XlsxSheet sheet, XlsxCell anchor, params (string Name, string? Value)[] fields) =>
        Output.RawRecords.Add(new RawRecord(kind, Ref(sheet, anchor),
            fields.Where(f => f.Value is not null).ToDictionary(f => f.Name, f => f.Value!)));
}

// Accumulates the proposals of every workbook in one import.
internal sealed class ImportCollector
{
    public List<ProposedAccount> Accounts { get; } = [];

    public List<ProposedBalance> Balances { get; } = [];

    public List<ProposedDebt> Debts { get; } = [];

    public List<ProposedBill> Bills { get; } = [];

    public List<ProposedIncome> Income { get; } = [];

    public ProposedSettings? Settings { get; set; }

    public List<ImportFinding> Findings { get; } = [];

    public List<RawRecord> RawRecords { get; } = [];

    public void Block(string code, string title, string detail, IReadOnlyList<SourceRef>? sources = null, string? subject = null) =>
        Findings.Add(new ImportFinding(code, FindingSeverity.Blocking, title, detail, sources ?? [], subject));

    public void Review(string code, string title, string detail, IReadOnlyList<SourceRef>? sources = null, string? subject = null) =>
        Findings.Add(new ImportFinding(code, FindingSeverity.NeedsReview, title, detail, sources ?? [], subject));

    public void Note(string code, string title, string detail, IReadOnlyList<SourceRef>? sources = null, string? subject = null) =>
        Findings.Add(new ImportFinding(code, FindingSeverity.Info, title, detail, sources ?? [], subject));
}

internal static class Labels
{
    // "Balance-as-of Date", "Balance As-Of Date " and "balance as of date" are the same label.
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(ch);
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    public static string Slug(string text) => Normalize(text).Replace(' ', '-');

    // "Card Workbook.xlsx" → "Card": the name a person gave the file is the
    // best name for the account it describes.
    public static string AccountNameFromFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        foreach (var suffix in new[] { "workbook", "tracker", "sheet" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && stem.Length > suffix.Length)
            {
                stem = stem[..^suffix.Length].TrimEnd(' ', '-', '_');
            }
        }

        return stem.Length == 0 ? Path.GetFileNameWithoutExtension(fileName) : stem;
    }
}
