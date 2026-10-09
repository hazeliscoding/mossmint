using System.Security.Cryptography;
using System.Text;
using Mossmint.Connectors.Abstractions;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Import;

// Normalizes a connector sync into the same batch a workbook import produces,
// so connectors get the same validation, reconciliation, dry run, review queue
// and atomic commit. Nothing here talks to a provider.
public static class ConnectorBatches
{
    // A sync older than this is stale: its balances may no longer be true.
    public const int StaleAfterDays = 3;

    public static ImportBatch From(ConnectorSync sync, string displayName, DateOnly today, Currency currency)
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

        return new ImportBatch($"connector:{sync.Connector}", ImportOrigin.Connector, today, currency, [file])
        {
            Accounts = [.. sync.Accounts.Select(a => new ProposedAccount(AccountKey(sync.Connector, a.ExternalId), a.Name, a.Kind, [new SourceRef(scope, $"account:{a.ExternalId}")]))],
            Balances = [.. sync.Balances.Where(b => known.Contains(b.AccountExternalId)).Select(b => new ProposedBalance(
                AccountKey(sync.Connector, b.AccountExternalId), b.Balance, b.AsOf, $"Reported by {displayName}", new SourceRef(scope, $"balance:{b.ObservationId}")))],
            Transactions = [.. sync.Transactions.Where(t => known.Contains(t.AccountExternalId)).Select(t => new ProposedTransaction(
                AccountKey(sync.Connector, t.AccountExternalId), t.ExternalId, t.Date, t.Amount, t.IsPending ? TransactionStatus.Pending : TransactionStatus.Posted,
                t.Description, new SourceRef(scope, $"transaction:{t.ExternalId}")))],
            Tombstones = [.. sync.Removed.Select(r => new Tombstone(AccountKey(sync.Connector, r.AccountExternalId), r.TransactionExternalId,
                new SourceRef(scope, $"transaction:{r.TransactionExternalId}")))],
            Findings = findings,
        };
    }

    public static string Scope(ConnectorId connector) => $"connector:{connector}";

    public static string AccountKey(ConnectorId connector, string externalId) => $"{Scope(connector)}:{externalId}";
}
