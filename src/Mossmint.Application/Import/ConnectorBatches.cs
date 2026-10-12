using System.Security.Cryptography;
using System.Text;
using Mossmint.Connectors.Abstractions;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Import;

// Normalizes a connector sync into the same batch a file import produces, so
// connectors get the same validation, reconciliation, dry run, review queue
// and atomic commit. Nothing here talks to a provider.
public static class ConnectorBatches
{
    // A sync older than this is stale: its balances may no longer be true.
    public const int StaleAfterDays = 3;

    // Destinations maps each of the source's account IDs to the Mossmint account
    // the user linked it to (one that exists, or one this import creates).
    // Unlinked accounts are left out.
    public static ImportBatch From(ConnectorSync sync, string displayName, DateOnly today, Currency currency, IReadOnlyDictionary<string, Account> destinations)
    {
        var scope = Scope(sync.Connector);
        var file = new SourceFileInfo(scope, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope))), 0, displayName);
        var findings = new List<ImportFinding>();
        var fetched = DateOnly.FromDateTime(sync.FetchedAt.UtcDateTime);
        var age = today.DayNumber - fetched.DayNumber;
        if (age > StaleAfterDays)
        {
            findings.Add(new ImportFinding("connector-stale", FindingSeverity.NeedsReview, $"{displayName} data is {age} days old",
                $"The last successful sync was on {ImportText.Date(fetched)}. Balances from it are kept with their own dates; refresh before relying on them.",
                [new SourceRef(scope, "")], scope));
        }

        var known = sync.Accounts.Select(a => a.ExternalId).ToHashSet();
        foreach (var orphan in sync.Balances.Select(b => b.AccountExternalId).Concat(sync.Transactions.Select(t => t.AccountExternalId))
                     .Where(id => !known.Contains(id)).Distinct())
        {
            findings.Add(new ImportFinding("connector-unknown-account", FindingSeverity.Blocking, $"{displayName} sent data for an unlisted account",
                $"Account '{orphan}' isn't in the sync's account list, so its data can't be placed.", [new SourceRef(scope, $"account:{orphan}")], scope));
        }

        foreach (var account in sync.Accounts.Where(a => !destinations.ContainsKey(a.ExternalId)))
        {
            findings.Add(new ImportFinding("connector-account-skipped", FindingSeverity.Info, $"{account.Name} left out",
                $"It isn't linked to a Mossmint account, so {displayName} data for it wasn't imported.", [new SourceRef(scope, $"account:{account.ExternalId}")], scope));
        }

        foreach (var (id, account) in destinations.Where(d => !known.Contains(d.Key)))
        {
            findings.Add(new ImportFinding("connector-account-missing", FindingSeverity.NeedsReview, $"{account.Name} is no longer shared by {displayName}",
                "The connection stopped sending this account. What it brought in before is kept; share it again at the provider, or unlink it.",
                [new SourceRef(scope, $"account:{id}")], ImportKeys.Account(account.Id)));
        }

        foreach (var error in sync.Errors)
        {
            var name = error.AccountExternalId is { } id && destinations.TryGetValue(id, out var linked) ? linked.Name : displayName;
            findings.Add(new ImportFinding("connector-error", FindingSeverity.NeedsReview, $"{name}: {displayName} reported a problem", error.Message,
                [new SourceRef(scope, error.AccountExternalId is { } a ? $"account:{a}" : "")], error.AccountExternalId ?? scope));
        }

        Account? Linked(string externalId) => destinations.GetValueOrDefault(externalId);
        Money Signed(string externalId, Money amount) => Linked(externalId)!.IsLiability ? -amount : amount;
        string Key(string externalId) => ImportKeys.Account(Linked(externalId)!.Id);
        string TransactionId(string externalId) => $"{sync.Connector}:{externalId}";

        return new ImportBatch($"connector:{sync.Connector}", ImportOrigin.Connector, today, currency, [file])
        {
            Accounts = [.. sync.Accounts.Where(a => Linked(a.ExternalId) is not null).Select(a => Linked(a.ExternalId)!)
                .Select(a => new ProposedAccount(ImportKeys.Account(a.Id), a.Name, a.Kind, [new SourceRef(scope, "")]))],
            Balances = [.. sync.Balances.Where(b => known.Contains(b.AccountExternalId) && Linked(b.AccountExternalId) is not null).Select(b => new ProposedBalance(
                Key(b.AccountExternalId), Signed(b.AccountExternalId, b.Balance), b.AsOf, $"Reported by {displayName}", new SourceRef(scope, $"balance:{b.ObservationId}")))],
            Transactions = [.. sync.Transactions.Where(t => known.Contains(t.AccountExternalId) && Linked(t.AccountExternalId) is not null).Select(t => new ProposedTransaction(
                Key(t.AccountExternalId), TransactionId(t.ExternalId), t.Date, Signed(t.AccountExternalId, t.Amount),
                t.IsPending ? TransactionStatus.Pending : TransactionStatus.Posted, t.Description, new SourceRef(scope, $"transaction:{t.ExternalId}")))],
            Tombstones = [.. sync.Removed.Where(r => Linked(r.AccountExternalId) is not null).Select(r => new Tombstone(Key(r.AccountExternalId), TransactionId(r.TransactionExternalId),
                new SourceRef(scope, $"transaction:{r.TransactionExternalId}")))],
            Findings = findings,
        };
    }

    public static string Scope(ConnectorId connector) => $"connector:{connector}";

    // The start of every provenance reference this connector's batches write
    // (ImportBatch.Reference: the scope's hash prefix, then the location).
    public static string ReferencePrefix(ConnectorId connector) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Scope(connector))))[..12] + ":";
}
