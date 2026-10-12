using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mossmint.Import.Transactions;

public enum OfxAccountType
{
    Checking,
    Savings,
    MoneyMarket,
    CreditLine,
    CreditCard,
    Other,
}

// Amounts are signed from the account holder's side, as OFX sends them:
// negative is money out, for cards too.
public sealed record OfxTransaction(string FitId, DateOnly Posted, decimal Amount, string Description);

public sealed record OfxBalance(decimal Amount, DateOnly AsOf);

// Problems lists what couldn't be read, in words, so the import can block
// instead of guessing.
public sealed record OfxStatement(
    string? BankId,
    string AccountId,
    OfxAccountType Type,
    string Currency,
    IReadOnlyList<OfxTransaction> Transactions,
    OfxBalance? Ledger,
    IReadOnlyList<string> Problems);

public sealed record OfxFile(IReadOnlyList<OfxStatement> Statements);

public sealed class OfxFormatException(string message) : FormatException(message);

// A hand-written reader for OFX and QFX statements, both the SGML of OFX 1.x
// (leaf elements never close) and the XML of 2.x. It is not an XML parser:
// it refuses document types and entity declarations outright, resolves only
// the five standard entities and character references, and caps nesting.
public static partial class OfxReader
{
    private const int MaxDepth = 32;

    public static OfxFile Read(byte[] content)
    {
        var text = Decode(content);
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new OfxFormatException("The file declares a document type or entities, which statement files never need.");
        }

        var start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new OfxFormatException("This isn't an OFX or QFX statement file.");
        }

        var root = Parse(text, start);
        return new OfxFile([.. root.Descendants().Where(n => n.Name is "STMTRS" or "CCSTMTRS").Select(Statement)]);
    }

    private static OfxStatement Statement(Node statement)
    {
        var problems = new List<string>();
        var isCard = statement.Name == "CCSTMTRS";
        var from = statement.Child(isCard ? "CCACCTFROM" : "BANKACCTFROM");
        var account = from?.Text("ACCTID") ?? "";
        if (account.Length == 0)
        {
            problems.Add("A statement has no account number, so Mossmint can't tell which account it belongs to.");
        }

        var type = isCard ? OfxAccountType.CreditCard : from?.Text("ACCTTYPE")?.ToUpperInvariant() switch
        {
            "CHECKING" => OfxAccountType.Checking,
            "SAVINGS" => OfxAccountType.Savings,
            "MONEYMRKT" => OfxAccountType.MoneyMarket,
            "CREDITLINE" => OfxAccountType.CreditLine,
            _ => OfxAccountType.Other,
        };

        var transactions = new List<OfxTransaction>();
        var number = 0;
        foreach (var t in statement.Child("BANKTRANLIST")?.Children.Where(c => c.Name == "STMTTRN") ?? [])
        {
            number++;
            var description = t.Text("NAME") ?? t.Child("PAYEE")?.Text("NAME") ?? t.Text("MEMO") ?? t.Text("TRNTYPE") ?? "";
            var label = $"Transaction {number} ({description})";
            var fitId = t.Text("FITID");
            if (string.IsNullOrEmpty(fitId))
            {
                problems.Add($"{label} has no FITID, so it can't be told apart from the bank's other transactions.");
            }
            else if (!TryDate(t.Text("DTPOSTED"), out var posted))
            {
                problems.Add($"{label}: '{t.Text("DTPOSTED")}' isn't a date.");
            }
            else if (!TryAmount(t.Text("TRNAMT"), out var amount))
            {
                problems.Add($"{label}: '{t.Text("TRNAMT")}' isn't an amount.");
            }
            else
            {
                transactions.Add(new OfxTransaction(fitId, posted, amount, description));
            }
        }

        OfxBalance? ledger = null;
        if (statement.Child("LEDGERBAL") is { } balance)
        {
            if (TryAmount(balance.Text("BALAMT"), out var amount) && TryDate(balance.Text("DTASOF"), out var asOf))
            {
                ledger = new OfxBalance(amount, asOf);
            }
            else
            {
                problems.Add($"The balance '{balance.Text("BALAMT")}' as of '{balance.Text("DTASOF")}' can't be read.");
            }
        }

        return new OfxStatement(from?.Text("BANKID"), account, type, statement.Text("CURDEF")?.ToUpperInvariant() ?? "", transactions, ledger, problems);
    }

    // The statement's own local date: the first eight digits of an OFX date-time.
    private static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        return text is { Length: >= 8 } && DateOnly.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // OFX amounts have no thousands separators; a few banks write a decimal comma.
    private static bool TryAmount(string? text, out decimal amount)
    {
        amount = 0;
        return text is not null && OfxAmount().IsMatch(text)
            && decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    // A 1.x header names its character set; 2.x is XML and UTF-8 unless it says otherwise.
    private static string Decode(byte[] content)
    {
        var head = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 1024));
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        return WesternHeader().IsMatch(head)
            ? TextDecoding.Windows1252.GetString(bytes)
            : TextDecoding.StrictUtf8(bytes) ?? TextDecoding.Windows1252.GetString(bytes);
    }

    private static Node Parse(string text, int start)
    {
        var root = new Node("#root");
        var open = new Stack<Node>([root]);
        Node? leaf = null;
        var i = start;
        while (text.IndexOf('<', i) is var lt and >= 0)
        {
            var gt = text.IndexOf('>', lt);
            if (gt < 0)
            {
                throw new OfxFormatException("A tag in the file never ends.");
            }

            var tag = text[(lt + 1)..gt].Trim();
            i = gt + 1;
            if (tag.StartsWith("!--", StringComparison.Ordinal))
            {
                i = text.IndexOf("-->", lt, StringComparison.Ordinal) is var end and >= 0 ? end + 3 : text.Length;
                continue;
            }

            if (tag.Length == 0 || tag[0] is '?' or '!')
            {
                continue;
            }

            if (tag[0] == '/')
            {
                var closing = tag[1..].Trim().ToUpperInvariant();
                if (leaf?.Name == closing)
                {
                    // XML closes its leaves; SGML leaves end at the next tag.
                    leaf = null;
                }
                else if (open.Any(n => n.Name == closing))
                {
                    while (open.Pop().Name != closing)
                    {
                    }
                }

                leaf = null;
                continue;
            }

            var selfClosing = tag.EndsWith('/');
            var node = new Node(tag.TrimEnd('/').Split((char[])[' ', '\t', '\r', '\n'], 2)[0].ToUpperInvariant());
            open.Peek().Children.Add(node);
            var next = text.IndexOf('<', i);
            var raw = next < 0 ? text[i..] : text[i..next];
            if (selfClosing)
            {
                leaf = null;
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                node.Value = Unescape(raw.Trim());
                leaf = node;
                i = next < 0 ? text.Length : next;
            }
            else
            {
                open.Push(node);
                leaf = null;
                if (open.Count > MaxDepth)
                {
                    throw new OfxFormatException("The file is nested too deeply to be a statement.");
                }
            }
        }

        return root;
    }

    private static string Unescape(string value) => Entity().Replace(value, m => m.Groups[1].Value switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        "nbsp" => " ",
        var n when n.StartsWith("#x", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) && c is > 0 and <= 0x10FFFF => char.ConvertFromUtf32(c),
        var n when n.StartsWith('#') && int.TryParse(n[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c is > 0 and <= 0x10FFFF => char.ConvertFromUtf32(c),
        _ => m.Value,
    });

    [GeneratedRegex(@"&(#[xX][0-9a-fA-F]{1,6}|#\d{1,7}|amp|lt|gt|quot|apos|nbsp);")]
    private static partial Regex Entity();

    [GeneratedRegex(@"^[+-]?\d+([.,]\d+)?$")]
    private static partial Regex OfxAmount();

    [GeneratedRegex(@"CHARSET:\s*(1252|ISO-8859-1|WINDOWS-1252)|encoding=""(windows-1252|iso-8859-1)""", RegexOptions.IgnoreCase)]
    private static partial Regex WesternHeader();

    private sealed class Node(string name)
    {
        public string Name { get; } = name;

        public string? Value { get; set; }

        public List<Node> Children { get; } = [];

        public Node? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

        public string? Text(string name) => Child(name)?.Value;

        public IEnumerable<Node> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
    }
}
