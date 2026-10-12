using System.Text;
using System.Text.RegularExpressions;

namespace Mossmint.Import.Transactions;

public enum CsvEncoding
{
    Utf8,
    Utf16,

    // A guess: bytes that aren't valid UTF-8 are read as Windows-1252, and the
    // preview says so, because accented names may then look wrong.
    Windows1252,
}

// Line is where the record starts in the file, for findings and the preview.
public sealed record CsvRow(int Line, IReadOnlyList<string> Fields);

public sealed record CsvTable(
    CsvEncoding Encoding,
    char Delimiter,
    bool HasHeader,
    IReadOnlyList<string> Header,
    IReadOnlyList<CsvRow> Rows,
    int SkippedLines);

public sealed class CsvFormatException(string message) : FormatException(message);

// A hand-written RFC 4180 reader, lenient where banks are sloppy: any line
// ending, blank lines, text after a closing quote, and a preamble of account
// details before the header. It never evaluates anything it reads.
public static partial class CsvReader
{
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];
    private const int SampleRecords = 50;

    public static CsvTable Read(byte[] content)
    {
        var (text, encoding) = Decode(content);

        // The delimiter that splits the most records into the same number of
        // fields wins; a semicolon file with decimal commas still splits evenly.
        var delimiter = Delimiters
            .Select((d, order) => (d, order, score: Consistency(Parse(text, d, SampleRecords))))
            .OrderByDescending(x => x.score.Ratio).ThenByDescending(x => x.score.Width).ThenBy(x => x.order)
            .First().d;
        var records = Parse(text, delimiter, int.MaxValue);
        if (records.Count == 0)
        {
            throw new CsvFormatException("The file has no rows.");
        }

        var width = records.GroupBy(r => r.Fields.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var first = records.FindIndex(r => r.Fields.Count == width);
        var hasHeader = !records[first].Fields.Any(LooksLikeData);
        IReadOnlyList<string> header = hasHeader
            ? [.. records[first].Fields.Select(f => f.Trim())]
            : [.. Enumerable.Range(1, width).Select(i => $"Column {i}")];
        return new CsvTable(encoding, delimiter, hasHeader, header, records[(hasHeader ? first + 1 : first)..], first);
    }

    private static (string Text, CsvEncoding Encoding) Decode(byte[] content)
    {
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (TextDecoding.StrictUtf8(bytes[3..]) ?? throw new CsvFormatException("The file says it is UTF-8 but isn't."), CsvEncoding.Utf8);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode.GetString(bytes[2..]), CsvEncoding.Utf16);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode.GetString(bytes[2..]), CsvEncoding.Utf16);
        }

        return TextDecoding.StrictUtf8(bytes) is { } utf8
            ? (utf8, CsvEncoding.Utf8)
            : (TextDecoding.Windows1252.GetString(bytes), CsvEncoding.Windows1252);
    }

    private static (double Ratio, int Width) Consistency(List<CsvRow> records)
    {
        if (records.Count == 0)
        {
            return (0, 0);
        }

        var mode = records.GroupBy(r => r.Fields.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
        return mode.Key < 2 ? (0, 0) : ((double)mode.Count() / records.Count, mode.Key);
    }

    private static List<CsvRow> Parse(string text, char delimiter, int limit)
    {
        var records = new List<CsvRow>();
        var fields = new List<string>();
        var field = new StringBuilder();
        int line = 1, start = 1;
        bool quoted = false, wasQuoted = false;

        void End()
        {
            fields.Add(field.ToString());
            if (fields.Count > 1 || wasQuoted || !string.IsNullOrWhiteSpace(fields[0]))
            {
                records.Add(new CsvRow(start, [.. fields]));
            }

            fields.Clear();
            field.Clear();
            wasQuoted = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (quoted)
            {
                if (c == '"' && next == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n' || (c == '\r' && next != '\n'))
                    {
                        line++;
                    }

                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0 && !wasQuoted)
            {
                quoted = wasQuoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                wasQuoted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && next == '\n')
                {
                    i++;
                }

                End();
                start = ++line;
                if (records.Count >= limit)
                {
                    return records;
                }
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
        {
            throw new CsvFormatException($"A quoted field that starts on line {start} never ends.");
        }

        End();
        return records;
    }

    // Header labels are words; a date or an amount means the first row is data.
    private static bool LooksLikeData(string field) => DataPattern().IsMatch(field.Trim());

    [GeneratedRegex(@"^(\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}|\(?[-+]?[$€£]?\s?\d[\d,. ]*\)?-?)$")]
    private static partial Regex DataPattern();
}
