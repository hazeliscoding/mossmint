using Mossmint.Import.Excel.Xlsx;

namespace Mossmint.Import.Excel.Mapping;

// A "Settings" sheet: a label in column A and its value to the right.
internal sealed class LabelledValues
{
    private readonly Dictionary<string, (XlsxCell Label, XlsxCell? Value)> _rows = [];

    public LabelledValues(XlsxSheet sheet)
    {
        Sheet = sheet;
        foreach (var label in sheet.Column(1).Where(c => c.Kind == CellKind.Text))
        {
            var key = Labels.Normalize(label.Value);
            if (key.Length == 0 || _rows.ContainsKey(key))
            {
                continue;
            }

            var value = Enumerable.Range(2, 2).Select(col => sheet.Cell(label.Row, col)).FirstOrDefault(c => c is not null && c.Value.Length > 0);
            _rows[key] = (label, value);
        }
    }

    public XlsxSheet Sheet { get; }

    public bool Has(string label) => _rows.ContainsKey(Labels.Normalize(label));

    // The first label that exists wins, so renamed labels can be listed together.
    public XlsxCell? Value(params string[] labels) =>
        labels.Select(Labels.Normalize).Where(_rows.ContainsKey).Select(l => _rows[l].Value).FirstOrDefault(v => v is not null);

    public XlsxCell? LabelCell(params string[] labels) =>
        labels.Select(Labels.Normalize).Where(_rows.ContainsKey).Select(l => _rows[l].Label).FirstOrDefault();
}

// A table with one header row. Columns are found by header text, so inserted
// or reordered columns don't break the mapping.
internal sealed class HeaderTable
{
    private readonly Dictionary<string, int> _columns;

    private HeaderTable(XlsxSheet sheet, int headerRow, Dictionary<string, int> columns)
    {
        Sheet = sheet;
        HeaderRow = headerRow;
        _columns = columns;
    }

    public XlsxSheet Sheet { get; }

    public int HeaderRow { get; }

    public static HeaderTable? Find(XlsxSheet? sheet, params string[] requiredHeaders)
    {
        if (sheet is null)
        {
            return null;
        }

        var required = requiredHeaders.Select(Labels.Normalize).ToList();
        foreach (var row in sheet.Cells.Where(c => c.Kind == CellKind.Text).GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            var columns = new Dictionary<string, int>();
            foreach (var cell in row)
            {
                columns.TryAdd(Labels.Normalize(cell.Value), cell.Column);
            }

            if (required.All(columns.ContainsKey))
            {
                return new HeaderTable(sheet, row.Key, columns);
            }
        }

        return null;
    }

    public int? Column(params string[] headers) =>
        headers.Select(Labels.Normalize).Where(_columns.ContainsKey).Select(h => (int?)_columns[h]).FirstOrDefault();

    public XlsxCell? Cell(int row, params string[] headers) => Column(headers) is { } column ? Sheet.Cell(row, column) : null;

    // Data rows until the first row whose key column is empty.
    public IEnumerable<int> Rows(params string[] keyHeaders)
    {
        var key = Column(keyHeaders);
        if (key is null)
        {
            yield break;
        }

        for (var row = HeaderRow + 1; row <= Sheet.LastRow; row++)
        {
            var cell = Sheet.Cell(row, key.Value);
            if (cell is null || cell.Value.Length == 0)
            {
                yield break;
            }

            yield return row;
        }
    }
}
