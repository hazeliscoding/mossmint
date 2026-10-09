using System.Globalization;

namespace Mossmint.Import.Excel.Xlsx;

public enum CellKind
{
    Number,
    Text,
    Boolean,
    Error,
}

public enum SheetVisibility
{
    Visible,
    Hidden,
    VeryHidden,
}

// One stored cell. Value is the cached result the spreadsheet app saved; the
// importer never evaluates formulas, so a stale cache is caught by
// reconciliation, not hidden by recalculation.
public sealed record XlsxCell(
    int Row,
    int Column,
    CellKind Kind,
    string Value,
    string? Formula = null,
    string? NumberFormat = null,
    string? Fill = null,
    bool IsDateFormat = false)
{
    public string Address => XlsxAddress.Format(Row, Column);

    public bool HasFormula => Formula is not null;

    // Google Sheets exports formulas Excel lacks as __xludf.DUMMYFUNCTION("…")
    // with the last Google result as the cached value. Nothing can recompute them.
    public bool IsGoogleOnly => Formula?.Contains("__xludf.DUMMYFUNCTION", StringComparison.OrdinalIgnoreCase) == true;

    public decimal? Number =>
        Kind == CellKind.Number && decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    public string? Text => Kind == CellKind.Text ? Value : null;

    public bool? Boolean => Kind == CellKind.Boolean ? Value == "1" : null;
}

public sealed class XlsxSheet(string name, SheetVisibility visibility, IReadOnlyDictionary<(int Row, int Column), XlsxCell> cells)
{
    public string Name { get; } = name;

    public SheetVisibility Visibility { get; } = visibility;

    public IEnumerable<XlsxCell> Cells => cells.Values.OrderBy(c => c.Row).ThenBy(c => c.Column);

    public int LastRow => cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Row);

    public XlsxCell? Cell(int row, int column) => cells.GetValueOrDefault((row, column));

    public XlsxCell? Cell(string address)
    {
        var (row, column) = XlsxAddress.Parse(address);
        return Cell(row, column);
    }

    public IEnumerable<XlsxCell> Column(int column) => Cells.Where(c => c.Column == column);
}

public sealed class XlsxWorkbook(IReadOnlyList<XlsxSheet> sheets, bool date1904)
{
    public IReadOnlyList<XlsxSheet> Sheets { get; } = sheets;

    public bool Date1904 { get; } = date1904;

    public XlsxSheet? Sheet(string name) =>
        Sheets.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    // Serial day numbers count from 1899-12-30 (the 1900 system, which keeps
    // Lotus's phantom 29 Feb 1900 so serials from March 1900 on line up) or from
    // 1904-01-01. A fraction is a time of day and is dropped.
    public DateOnly ToDate(decimal serial)
    {
        var epoch = Date1904 ? new DateOnly(1904, 1, 1) : new DateOnly(1899, 12, 30);
        return epoch.AddDays((int)decimal.Floor(serial));
    }
}

public static class XlsxAddress
{
    public static string Format(int row, int column)
    {
        var letters = "";
        for (var c = column; c > 0; c = (c - 1) / 26)
        {
            letters = (char)('A' + ((c - 1) % 26)) + letters;
        }

        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    public static (int Row, int Column) Parse(string address)
    {
        var column = 0;
        var i = 0;
        for (; i < address.Length && char.IsAsciiLetter(address[i]); i++)
        {
            column = (column * 26) + (char.ToUpperInvariant(address[i]) - 'A' + 1);
        }

        if (column == 0 || i == address.Length || !int.TryParse(address.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1)
        {
            throw new FormatException($"'{address}' is not a cell address.");
        }

        return (row, column);
    }
}
