using System.Globalization;
using Mossmint.Connectors.Abstractions;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Samples.Connectors;

// A connector that replays scripted syncs, in order, with no network. Each
// response's cursor is its position, so a test can replay or skip ahead.
public sealed class ScriptedConnector(ConnectorId id, string displayName, IReadOnlyList<ConnectorSync> script) : IConnector
{
    public ConnectorId Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.Accounts | ConnectorCapabilities.Balances | ConnectorCapabilities.Transactions;

    public Task<ConnectorSync> SyncAsync(string? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = cursor is null ? 0 : int.Parse(cursor, CultureInfo.InvariantCulture) + 1;
        var sync = script[Math.Min(next, script.Count - 1)];
        return Task.FromResult(sync with { Cursor = Math.Min(next, script.Count - 1).ToString(CultureInfo.InvariantCulture) });
    }
}

// Invented accounts and activity for the Juniper household's fake bank.
public static class FakeBank
{
    public static readonly ConnectorId Id = new("fake-bank");

    public const string DisplayName = "Fake Bank";

    public static readonly ConnectorAccount Checking = new("chk-001", "Everyday Checking", AccountKind.Checking, Currency.Usd);

    public static readonly ConnectorAccount Savings = new("sav-002", "Rainy Day Savings", AccountKind.Savings, Currency.Usd);

    public static ConnectorSync First(DateTimeOffset fetchedAt) => new(
        Id, fetchedAt, null,
        [Checking, Savings],
        [
            new(Checking.ExternalId, "bal-1", Money.Usd(1_482.35m), new DateOnly(2026, 10, 8)),
            new(Savings.ExternalId, "bal-2", Money.Usd(640.00m), new DateOnly(2026, 10, 8)),
        ],
        [
            new(Checking.ExternalId, "t-100", new DateOnly(2026, 10, 6), Money.Usd(-42.18m), false, "Corner Grocer"),
            new(Checking.ExternalId, "t-101", new DateOnly(2026, 10, 7), Money.Usd(-12.50m), true, "Bean There Cafe"),
            new(Checking.ExternalId, "t-102", new DateOnly(2026, 10, 8), Money.Usd(-60.00m), true, "Fuel Stop"),
        ],
        []);

    // The next day: one pending charge posts, one drops, a new one arrives.
    public static ConnectorSync Second(DateTimeOffset fetchedAt) => new(
        Id, fetchedAt, "0",
        [Checking, Savings],
        [new(Checking.ExternalId, "bal-3", Money.Usd(1_409.85m), new DateOnly(2026, 10, 9))],
        [
            new(Checking.ExternalId, "t-101", new DateOnly(2026, 10, 7), Money.Usd(-12.50m), false, "Bean There Cafe"),
            new(Checking.ExternalId, "t-103", new DateOnly(2026, 10, 9), Money.Usd(-60.00m), false, "Fuel Stop"),
        ],
        [new(Checking.ExternalId, "t-102")]);
}
