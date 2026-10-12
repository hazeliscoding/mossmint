using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Import;

// How sources name what they propose. A workbook names records by what and
// where they are, and a connector by its own account IDs. A bank file lands in
// an account the user picked, or one the import creates, so its key names the
// Mossmint account itself and stays the same on every later import.
// A transaction's key is "<account key>|<external id>".
public static class ImportKeys
{
    private const string AccountPrefix = "account:";

    public static string Account(AccountId id) => AccountPrefix + id.Value;

    public static AccountId? AccountIn(string key) =>
        key.StartsWith(AccountPrefix, StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(AccountPrefix.Length), out var id) ? new AccountId(id) : null;

    // Where a transaction came from: "csv" or "ofx" for a bank file (its
    // external ID's prefix), "connector:<id>" for a connection.
    public static string SourceOf(string transactionKey)
    {
        var split = transactionKey.IndexOf('|', StringComparison.Ordinal);
        var account = transactionKey[..split];
        if (AccountIn(account) is not null)
        {
            var external = transactionKey[(split + 1)..];
            return external[..Math.Max(0, external.IndexOf(':', StringComparison.Ordinal))];
        }

        return account.StartsWith("connector:", StringComparison.Ordinal) ? string.Join(':', account.Split(':').Take(2)) : "workbook";
    }

    public static string Describe(string source) => source switch
    {
        "csv" => "CSV files",
        "ofx" => "bank statement files",
        _ when source.StartsWith("connector:", StringComparison.Ordinal) => $"a connection ({source["connector:".Length..]})",
        _ => "a workbook",
    };
}
