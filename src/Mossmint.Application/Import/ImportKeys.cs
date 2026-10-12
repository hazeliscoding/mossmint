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
}
