using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Connectors.Abstractions;

public readonly record struct ConnectorId(string Value)
{
    public override string ToString() => Value;
}

// What a provider can supply. The UI never advertises more than this: a
// provider without APRs is never shown as tracking APR changes.
[Flags]
public enum ConnectorCapabilities
{
    None = 0,
    Accounts = 1,
    Balances = 2,
    Transactions = 4,
    LiabilityTerms = 8,
    Statements = 16,
}

// Accounts, balances and transactions in the source's own terms. External IDs
// belong to the source and must stay stable from one sync to the next.
public sealed record ConnectorAccount(string ExternalId, string Name, AccountKind Kind, Currency Currency);

// A balance the source observed. AsOf is when the institution reported it,
// not when the sync ran: a sync's timestamp is not a balance's timestamp.
// Balances and amounts are from the account holder's side, positive meaning
// money in or held; the import turns them around for cards and loans.
public sealed record ConnectorBalance(string AccountExternalId, string ObservationId, Money Balance, DateOnly AsOf);

public sealed record ConnectorTransaction(
    string AccountExternalId,
    string ExternalId,
    DateOnly Date,
    Money Amount,
    bool IsPending,
    string Description);

// The source says a transaction it reported earlier no longer exists (a
// pending charge that dropped, or one the bank reversed).
public sealed record ConnectorRemoval(string AccountExternalId, string TransactionExternalId);

// A problem the source reported, safe to show the user, for one account or
// (AccountExternalId null) the whole connection.
public sealed record ConnectorError(string? AccountExternalId, string Message);

// One sync. Accounts lists every account the user shared, every time;
// balances, transactions and removals may be only what changed since Cursor.
public sealed record ConnectorSync(
    ConnectorId Connector,
    DateTimeOffset FetchedAt,
    string? Cursor,
    IReadOnlyList<ConnectorAccount> Accounts,
    IReadOnlyList<ConnectorBalance> Balances,
    IReadOnlyList<ConnectorTransaction> Transactions,
    IReadOnlyList<ConnectorRemoval> Removed)
{
    public IReadOnlyList<ConnectorError> Errors { get; init; } = [];
}

// A source of account data the user chose to connect. Nothing in the pure
// layers implements this; real connectors are separate projects with their
// own network exception and ADR (docs/connectors/ADR-provider-security.md).
public interface IConnector
{
    ConnectorId Id { get; }

    string DisplayName { get; }

    ConnectorCapabilities Capabilities { get; }

    // Pass the cursor from the previous sync to get only what changed since.
    Task<ConnectorSync> SyncAsync(string? cursor, CancellationToken cancellationToken);
}
