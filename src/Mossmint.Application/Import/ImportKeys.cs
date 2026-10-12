using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Import;

// How sources name what they propose. A workbook names records by what and
// where they are. A bank file or a connection lands in an account the user
// picked, or one the import creates, so its key names the Mossmint account
// itself and stays the same on every later import.
// A transaction's key is "<account key>|<source>:<the source's own ID>".
public static class ImportKeys
{
    private const string AccountPrefix = "account:";

    public static string Account(AccountId id) => AccountPrefix + id.Value;

    public static AccountId? AccountIn(string key) =>
        key.StartsWith(AccountPrefix, StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(AccountPrefix.Length), out var id) ? new AccountId(id) : null;

    // Where a transaction came from: its external ID's prefix, "csv" or "ofx"
    // for a bank file and the connector's ID for a connection.
    public static string SourceOf(string transactionKey)
    {
        var external = transactionKey[(transactionKey.IndexOf('|', StringComparison.Ordinal) + 1)..];
        return external[..Math.Max(0, external.IndexOf(':', StringComparison.Ordinal))];
    }

    public static string Describe(string source) => source switch
    {
        "csv" => "CSV files",
        "ofx" => "bank statement files",
        _ => $"a connection ({source})",
    };
}
