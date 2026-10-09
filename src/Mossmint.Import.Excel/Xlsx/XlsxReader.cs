using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Mossmint.Import.Excel.Xlsx;

public sealed class XlsxFormatException(string message, Exception? inner = null) : IOException(message, inner);

// Size caps that keep a hostile or damaged file from exhausting memory. Real
// budgeting workbooks are well under a megabyte.
public sealed record XlsxLimits(long MaxPartBytes = 32L * 1024 * 1024, long MaxTotalBytes = 128L * 1024 * 1024, int MaxEntries = 2_000)
{
    public static XlsxLimits Default { get; } = new();
}

// Reads the parts of an .xlsx file the importer needs: sheets, cached values,
// formulas, number formats and fill colours. It works on bytes already in
// memory, so it can never write to the source file. DTDs are refused and parts
// are read through a size cap rather than trusting the zip's own size fields.
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static XlsxWorkbook Read(byte[] content, XlsxLimits? limits = null)
    {
        limits ??= XlsxLimits.Default;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries)
            {
                throw new XlsxFormatException("The file has too many parts to be a workbook.");
            }

            var parts = new PartReader(zip, limits);
            var workbook = parts.Load("xl/workbook.xml") ?? throw new XlsxFormatException("This isn't an Excel workbook (no xl/workbook.xml).");
            var rels = parts.Load("xl/_rels/workbook.xml.rels");
            var targets = rels?.Root?.Elements(PackageRel + "Relationship")
                .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!) ?? [];

            var sharedStrings = ReadSharedStrings(parts.Load("xl/sharedStrings.xml"));
            var styles = Styles.From(parts.Load("xl/styles.xml"));
            var date1904 = workbook.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is { } flag && (flag.Value == "1" || flag.Value == "true");

            var sheets = new List<XlsxSheet>();
            foreach (var sheet in workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
            {
                var name = (string?)sheet.Attribute("name") ?? "";
                var id = (string?)sheet.Attribute(Rel + "id");
                if (id is null || !targets.TryGetValue(id, out var target))
                {
                    continue;
                }

                var visibility = (string?)sheet.Attribute("state") switch
                {
                    "hidden" => SheetVisibility.Hidden,
                    "veryHidden" => SheetVisibility.VeryHidden,
                    _ => SheetVisibility.Visible,
                };
                var document = parts.Load(ResolveTarget(target)) ?? throw new XlsxFormatException($"Sheet '{name}' is missing from the file.");
                sheets.Add(new XlsxSheet(name, visibility, ReadCells(document, sharedStrings, styles)));
            }

            return new XlsxWorkbook(sheets, date1904);
        }
        catch (InvalidDataException e)
        {
            throw new XlsxFormatException("The file isn't a readable .xlsx package.", e);
        }
        catch (XmlException e)
        {
            throw new XlsxFormatException("The workbook contains malformed XML.", e);
        }
    }

    private static string ResolveTarget(string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

    private static List<string> ReadSharedStrings(XDocument? document) =>
        document?.Root?.Elements(Main + "si").Select(TextOf).ToList() ?? [];

    // Rich text keeps its runs in <r><t>; phonetic guides (<rPh>) aren't part of the value.
    private static string TextOf(XElement container) =>
        string.Concat(container.Descendants(Main + "t").Where(t => t.Parent?.Name != Main + "rPh").Select(t => t.Value));

    private static Dictionary<(int, int), XlsxCell> ReadCells(XDocument document, List<string> sharedStrings, Styles styles)
    {
        var cells = new Dictionary<(int, int), XlsxCell>();
        var sharedFormulas = new Dictionary<string, string>();
        var data = document.Root?.Element(Main + "sheetData");
        var rowNumber = 0;
        foreach (var row in data?.Elements(Main + "row") ?? [])
        {
            rowNumber = row.Attribute("r") is { } r ? int.Parse(r.Value, CultureInfo.InvariantCulture) : rowNumber + 1;
            var columnNumber = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                (var cellRow, columnNumber) = c.Attribute("r") is { } address ? XlsxAddress.Parse(address.Value) : (rowNumber, columnNumber + 1);
                var formula = FormulaOf(c.Element(Main + "f"), sharedFormulas);
                var style = styles.ForIndex((int?)c.Attribute("s") ?? 0);
                var cell = ValueOf(c, sharedStrings) is { } value
                    ? new XlsxCell(cellRow, columnNumber, value.Kind, value.Text, formula, style.NumberFormat, style.Fill, style.IsDate)
                    : formula is not null || style.Fill is not null
                        ? new XlsxCell(cellRow, columnNumber, CellKind.Text, "", formula, style.NumberFormat, style.Fill, style.IsDate)
                        : null;
                if (cell is not null)
                {
                    cells[(cellRow, columnNumber)] = cell;
                }
            }
        }

        return cells;
    }

    private static (CellKind Kind, string Text)? ValueOf(XElement c, List<string> sharedStrings)
    {
        var type = (string?)c.Attribute("t") ?? "n";
        if (type == "inlineStr")
        {
            return c.Element(Main + "is") is { } inline ? (CellKind.Text, TextOf(inline)) : null;
        }

        if (c.Element(Main + "v")?.Value is not { } v)
        {
            return null;
        }

        return type switch
        {
            "s" when int.TryParse(v, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count =>
                (CellKind.Text, sharedStrings[index]),
            "s" => throw new XlsxFormatException("A cell points at a shared string that doesn't exist."),
            "str" or "d" => (CellKind.Text, v),
            "b" => (CellKind.Boolean, v.Trim() is "1" or "true" ? "1" : "0"),
            "e" => (CellKind.Error, v),
            _ => (CellKind.Number, v.Trim()),
        };
    }

    // A shared formula is written once with its range; the other cells refer to
    // it by index. Their text isn't re-addressed, which is fine for what the
    // importer asks of a formula: whether one exists and what it calls.
    private static string? FormulaOf(XElement? f, Dictionary<string, string> sharedFormulas)
    {
        if (f is null)
        {
            return null;
        }

        var text = f.Value;
        if ((string?)f.Attribute("t") == "shared" && (string?)f.Attribute("si") is { } index)
        {
            if (text.Length > 0)
            {
                sharedFormulas[index] = text;
            }
            else
            {
                text = sharedFormulas.GetValueOrDefault(index, "");
            }
        }

        return "=" + text;
    }

    private sealed class PartReader(ZipArchive zip, XlsxLimits limits)
    {
        private static readonly XmlReaderSettings Settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = true,
        };

        private long _total;

        public XDocument? Load(string path)
        {
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            var buffer = new MemoryStream();
            using (var stream = entry.Open())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    _total += read;
                    if (buffer.Length + read > limits.MaxPartBytes || _total > limits.MaxTotalBytes)
                    {
                        throw new XlsxFormatException("The workbook expands to more data than Mossmint will read.");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            buffer.Position = 0;
            using var reader = XmlReader.Create(buffer, Settings);
            return XDocument.Load(reader);
        }
    }

    private sealed record CellStyle(string? NumberFormat, string? Fill, bool IsDate);

    private sealed class Styles
    {
        // Built-in date and time formats (ECMA-376 §18.8.30).
        private static readonly HashSet<int> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47];

        private readonly List<CellStyle> _styles = [];

        public static Styles From(XDocument? document)
        {
            var styles = new Styles();
            var root = document?.Root;
            var formats = root?.Element(Main + "numFmts")?.Elements(Main + "numFmt")
                .ToDictionary(n => (int)n.Attribute("numFmtId")!, n => (string?)n.Attribute("formatCode") ?? "") ?? [];
            var fills = root?.Element(Main + "fills")?.Elements(Main + "fill").Select(FillColour).ToList() ?? [];
            foreach (var xf in root?.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
            {
                var formatId = (int?)xf.Attribute("numFmtId") ?? 0;
                var fillId = (int?)xf.Attribute("fillId") ?? 0;
                var code = formats.GetValueOrDefault(formatId);
                styles._styles.Add(new CellStyle(
                    code ?? (formatId == 0 ? null : $"builtin:{formatId}"),
                    fillId >= 0 && fillId < fills.Count ? fills[fillId] : null,
                    BuiltInDateFormats.Contains(formatId) || (code is not null && LooksLikeDate(code))));
            }

            return styles;
        }

        public CellStyle ForIndex(int index) =>
            index >= 0 && index < _styles.Count ? _styles[index] : new CellStyle(null, null, false);

        private static string? FillColour(XElement fill)
        {
            var pattern = fill.Element(Main + "patternFill");
            if (pattern is null || (string?)pattern.Attribute("patternType") is null or "none")
            {
                return null;
            }

            var colour = pattern.Element(Main + "fgColor");
            return colour switch
            {
                null => null,
                _ when colour.Attribute("rgb") is { } rgb => rgb.Value.ToUpperInvariant(),
                _ when colour.Attribute("theme") is { } theme => $"theme:{theme.Value}",
                _ when colour.Attribute("indexed") is { } indexed => $"indexed:{indexed.Value}",
                _ => null,
            };
        }

        // A custom format is a date when it has day or year codes outside quoted
        // text, escapes and [colour]/[locale] sections. Month-only formats (m)
        // are also minutes, so they don't count on their own.
        private static bool LooksLikeDate(string code)
        {
            var section = code.Split(';')[0];
            var inQuotes = false;
            var inBrackets = false;
            for (var i = 0; i < section.Length; i++)
            {
                var ch = section[i];
                switch (ch)
                {
                    case '"':
                        inQuotes = !inQuotes;
                        continue;
                    case '\\':
                        i++;
                        continue;
                    case '[' when !inQuotes:
                        inBrackets = true;
                        continue;
                    case ']' when !inQuotes:
                        inBrackets = false;
                        continue;
                }

                if (!inQuotes && !inBrackets && char.ToLowerInvariant(ch) is 'd' or 'y')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
