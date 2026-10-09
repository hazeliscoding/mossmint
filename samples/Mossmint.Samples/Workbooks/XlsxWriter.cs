using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Mossmint.Samples.Workbooks;

// Writes the minimal SpreadsheetML that synthetic workbooks need: shared
// strings, booleans, numbers, dates, fills, number formats, hidden sheets and
// formulas with whatever cached value the test wants (stale ones included).
// Output is deterministic, so the same workbook always has the same SHA-256.
public sealed class XlsxWriter
{
    private readonly List<SheetBuilder> _sheets = [];

    public bool Date1904 { get; init; }

    public SheetBuilder Sheet(string name, bool hidden = false)
    {
        var sheet = new SheetBuilder(name, hidden, this);
        _sheets.Add(sheet);
        return sheet;
    }

    public byte[] ToBytes()
    {
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var styles = new StyleTable();
        var sheetXml = _sheets.Select(s => s.ToXml(text => Intern(text, strings, stringIndex), styles)).ToList();

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes());
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", WorkbookXml());
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
            Add(zip, "xl/styles.xml", styles.ToXml());
            Add(zip, "xl/sharedStrings.xml", SharedStringsXml(strings));
            for (var i = 0; i < sheetXml.Count; i++)
            {
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sheetXml[i]);
            }
        }

        return output.ToArray();
    }

    internal static decimal Serial(DateOnly date, bool date1904) =>
        date.DayNumber - (date1904 ? new DateOnly(1904, 1, 1) : new DateOnly(1899, 12, 30)).DayNumber;

    private static int Intern(string text, List<string> strings, Dictionary<string, int> index)
    {
        if (!index.TryGetValue(text, out var i))
        {
            i = strings.Count;
            strings.Add(text);
            index[text] = i;
        }

        return i;
    }

    private static void Add(ZipArchive zip, string path, string xml)
    {
        // A fixed timestamp keeps the bytes, and so the hash, stable.
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(xml);
        stream.Write(bytes);
    }

    private string ContentTypes()
    {
        var sheets = string.Concat(_sheets.Select((_, i) =>
            $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>{sheets}</Types>""";
    }

    private string WorkbookXml()
    {
        var sheets = string.Concat(_sheets.Select((s, i) =>
            $"""<sheet name="{Escape(s.Name)}" sheetId="{i + 1}"{(s.Hidden ? " state=\"hidden\"" : "")} r:id="rId{i + 1}"/>"""));
        var properties = Date1904 ? """<workbookPr date1904="1"/>""" : "<workbookPr/>";
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">{properties}<sheets>{sheets}</sheets></workbook>""";
    }

    private string WorkbookRels()
    {
        var count = _sheets.Count;
        var sheets = string.Concat(_sheets.Select((_, i) =>
            $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{sheets}<Relationship Id="rId{count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId{count + 2}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>""";
    }

    private static string SharedStringsXml(List<string> strings) =>
        $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{strings.Count}" uniqueCount="{strings.Count}">{string.Concat(strings.Select(s => $"""<si><t xml:space="preserve">{Escape(s)}</t></si>"""))}</sst>""";

    internal static string Escape(string text) => SecurityElement.Escape(text);

    public sealed class SheetBuilder
    {
        private readonly SortedDictionary<(int Row, int Column), Cell> _cells = [];
        private readonly XlsxWriter _owner;

        internal SheetBuilder(string name, bool hidden, XlsxWriter owner)
        {
            Name = name;
            Hidden = hidden;
            _owner = owner;
        }

        public string Name { get; }

        public bool Hidden { get; }

        // Value is the cached value: string, bool, a number type, DateOnly or null.
        public SheetBuilder Set(string address, object? value, string? formula = null, string? format = null, string? fill = null)
        {
            var (row, column) = Parse(address);
            if (value is DateOnly date)
            {
                value = Serial(date, _owner.Date1904);
                format ??= "m/d/yyyy";
            }

            _cells[(row, column)] = new Cell(value, formula, format, fill);
            return this;
        }

        internal string ToXml(Func<string, int> intern, StyleTable styles)
        {
            var body = new StringBuilder();
            foreach (var row in _cells.GroupBy(c => c.Key.Row))
            {
                body.Append(CultureInfo.InvariantCulture, $"<row r=\"{row.Key}\">");
                foreach (var (key, cell) in row)
                {
                    var address = Address(key.Row, key.Column);
                    var style = styles.IndexFor(cell.Format, cell.Fill);
                    var s = style == 0 ? "" : $" s=\"{style}\"";
                    var f = cell.Formula is null ? "" : $"<f>{Escape(cell.Formula.TrimStart('='))}</f>";
                    body.Append(cell.Value switch
                    {
                        null => $"<c r=\"{address}\"{s}>{f}</c>",
                        string text when cell.Formula is not null => $"<c r=\"{address}\"{s} t=\"str\">{f}<v>{Escape(text)}</v></c>",
                        string text => $"<c r=\"{address}\"{s} t=\"s\"><v>{intern(text)}</v></c>",
                        bool flag => $"<c r=\"{address}\"{s} t=\"b\">{f}<v>{(flag ? 1 : 0)}</v></c>",
                        IFormattable number => $"<c r=\"{address}\"{s}>{f}<v>{number.ToString(null, CultureInfo.InvariantCulture)}</v></c>",
                        _ => throw new ArgumentException($"Unsupported value type {cell.Value.GetType().Name}."),
                    });
                }

                body.Append("</row>");
            }

            return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{body}</sheetData></worksheet>""";
        }

        private static (int Row, int Column) Parse(string address)
        {
            var column = 0;
            var i = 0;
            for (; i < address.Length && char.IsAsciiLetter(address[i]); i++)
            {
                column = (column * 26) + (char.ToUpperInvariant(address[i]) - 'A' + 1);
            }

            return (int.Parse(address.AsSpan(i), CultureInfo.InvariantCulture), column);
        }

        private static string Address(int row, int column)
        {
            var letters = "";
            for (var c = column; c > 0; c = (c - 1) / 26)
            {
                letters = (char)('A' + ((c - 1) % 26)) + letters;
            }

            return letters + row.ToString(CultureInfo.InvariantCulture);
        }

        private sealed record Cell(object? Value, string? Formula, string? Format, string? Fill);
    }

    internal sealed class StyleTable
    {
        private readonly List<string> _formats = [];
        private readonly List<string> _fills = [];
        private readonly List<(int Format, int Fill)> _xfs = [(0, 0)];

        public int IndexFor(string? format, string? fill)
        {
            if (format is null && fill is null)
            {
                return 0;
            }

            var formatId = format is null ? 0 : 164 + IndexOf(_formats, format);
            var fillId = fill is null ? 0 : 2 + IndexOf(_fills, fill);
            var xf = (formatId, fillId);
            var index = _xfs.IndexOf(xf);
            if (index < 0)
            {
                index = _xfs.Count;
                _xfs.Add(xf);
            }

            return index;
        }

        public string ToXml()
        {
            var formats = string.Concat(_formats.Select((f, i) => $"""<numFmt numFmtId="{164 + i}" formatCode="{Escape(f)}"/>"""));
            var fills = string.Concat(_fills.Select(f => $"""<fill><patternFill patternType="solid"><fgColor rgb="{f}"/><bgColor indexed="64"/></patternFill></fill>"""));
            var xfs = string.Concat(_xfs.Select(x => $"""<xf numFmtId="{x.Format}" fontId="0" fillId="{x.Fill}" borderId="0" xfId="0"{(x.Format != 0 ? " applyNumberFormat=\"1\"" : "")}{(x.Fill != 0 ? " applyFill=\"1\"" : "")}/>"""));
            return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="{_formats.Count}">{formats}</numFmts><fonts count="1"><font><sz val="10"/><name val="Arial"/></font></fonts><fills count="{_fills.Count + 2}"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>{fills}</fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="{_xfs.Count}">{xfs}</cellXfs></styleSheet>""";
        }

        private static int IndexOf(List<string> list, string value)
        {
            var index = list.IndexOf(value);
            if (index < 0)
            {
                index = list.Count;
                list.Add(value);
            }

            return index;
        }
    }
}
