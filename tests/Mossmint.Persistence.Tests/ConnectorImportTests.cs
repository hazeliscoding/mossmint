using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Connectors.Abstractions;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Import.Excel;
using Mossmint.Samples.Connectors;

namespace Mossmint.Persistence.Tests;

// Connector data takes the same path as a workbook: normalize, plan, review, commit.
public sealed class ConnectorImportTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateTimeOffset Morning = new(2026, 10, 9, 13, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero));
    private readonly ImportService _imports;
    private readonly IProfileSession _session;

    // The accounts the user linked the fake bank's accounts to, created by the first sync.
    private readonly Dictionary<string, Account> _linked = new()
    {
        [FakeBank.Checking.ExternalId] = new(new AccountId(Guid.CreateVersion7()), "Household Checking", AccountKind.Checking, Currency.Usd),
        [FakeBank.Savings.ExternalId] = new(new AccountId(Guid.CreateVersion7()), "Rainy Day Savings", AccountKind.Savings, Currency.Usd),
    };

    public ConnectorImportTests()
    {
        _imports = new ImportService(new WorkbookImporter(), _clock);
        _session = _root.Store(_clock).Create("Juniper", "moss under the old oak", unlockOnThisDevice: false).Session;
    }

    public void Dispose()
    {
        _session.Dispose();
        _root.Dispose();
    }

    [Fact]
    public async Task A_first_sync_brings_in_accounts_balances_and_transactions_as_connector_data()
    {
        var connector = new ScriptedConnector(FakeBank.Id, FakeBank.DisplayName, [FakeBank.First(Morning)]);

        var plan = Apply(await connector.SyncAsync(null, CancellationToken.None));

        plan.Count(ChangeKind.Add).Should().Be(2 + 2 + 3);
        var checking = _linked[FakeBank.Checking.ExternalId];
        _session.Data.Accounts().Should().Contain(a => a.Id == checking.Id && a.Name == "Household Checking");
        _session.Data.Balances(checking.Id).Single().Provenance.Source.Should().Be(SourceKind.Connector);
        _session.Imports.Transactions().Should().HaveCount(3).And.OnlyContain(t => t.Transaction.Provenance.Source == SourceKind.Connector);
        _session.Imports.ReviewItems().Should().BeEmpty();
    }

    [Fact]
    public void Replaying_a_sync_changes_nothing()
    {
        Apply(FakeBank.First(Morning));
        var before = _session.Data.Counts();

        var replay = Apply(FakeBank.First(Morning));

        replay.Changes.Should().OnlyContain(c => c.Kind == ChangeKind.Unchanged);
        var after = _session.Data.Counts();
        after.Should().Be(before with { AuditEvents = after.AuditEvents, ImportRuns = before.ImportRuns + 1 });
    }

    [Fact]
    public void A_pending_charge_that_posts_is_updated_in_place()
    {
        Apply(FakeBank.First(Morning));

        var plan = Apply(FakeBank.Second(Morning.AddDays(1)));

        plan.Changes.Should().ContainSingle(c => c.Key.EndsWith("|fake-bank:t-101", StringComparison.Ordinal)).Which.Kind.Should().Be(ChangeKind.Update);
        _session.Imports.Transactions().Single(t => t.Key.EndsWith("|fake-bank:t-101", StringComparison.Ordinal))
            .Transaction.Status.Should().Be(TransactionStatus.Posted);
        _session.Imports.Transactions().Should().HaveCount(4);
    }

    [Fact]
    public void A_removed_transaction_is_flagged_and_kept_never_deleted()
    {
        Apply(FakeBank.First(Morning));

        var plan = Apply(FakeBank.Second(Morning.AddDays(1)));

        plan.Changes.Should().ContainSingle(c => c.Kind == ChangeKind.RemovedAtSource);
        var removed = _session.Imports.Transactions().Single(t => t.Key.EndsWith("|fake-bank:t-102", StringComparison.Ordinal));
        removed.RemovedAtSource.Should().BeTrue();
        removed.Transaction.Description.Should().Be("Fuel Stop");
        _session.Imports.ReviewItems().Should().ContainSingle(r => r.Code == "removed-at-source");

        Apply(FakeBank.Second(Morning.AddDays(1))).Changes.Should().NotContain(c => c.Kind == ChangeKind.RemovedAtSource, "a tombstone is applied once");
    }

    [Fact]
    public void A_stale_sync_is_queued_for_review()
    {
        Apply(FakeBank.First(Morning.AddDays(-10)));

        _session.Imports.ReviewItems().Should().ContainSingle(r => r.Code == "connector-stale").Which.Title.Should().Contain("10 days old");
    }

    [Fact]
    public void An_older_balance_arriving_late_never_replaces_a_newer_one()
    {
        Apply(FakeBank.Second(Morning.AddDays(1)) with { Transactions = [], Removed = [] });
        var late = FakeBank.First(Morning) with { Transactions = [] };

        Apply(late);

        var balances = _session.Data.Balances(_linked[FakeBank.Checking.ExternalId].Id);
        balances.Should().HaveCount(2);
        balances[0].Balance.Should().Be(Money.Usd(1_409.85m), "the newest observation stays current");
    }

    [Fact]
    public void An_account_dropped_from_a_sync_is_kept_and_queued()
    {
        Apply(FakeBank.First(Morning));

        var plan = Apply(FakeBank.Second(Morning.AddDays(1)) with { Accounts = [FakeBank.Checking] });

        plan.Findings.Should().ContainSingle(f => f.Code == "connector-account-missing").Which.Title.Should().Contain("Rainy Day Savings");
        _session.Data.Accounts().Should().Contain(a => a.Name == "Rainy Day Savings");
    }

    [Fact]
    public void An_account_left_unlinked_is_left_out_and_errors_are_queued_per_account()
    {
        _linked.Remove(FakeBank.Savings.ExternalId);
        var sync = FakeBank.First(Morning) with { Errors = [new ConnectorError(FakeBank.Checking.ExternalId, "Sign in again at your bank.")] };

        var plan = Apply(sync);

        _session.Data.Accounts().Should().ContainSingle();
        plan.Findings.Should().Contain(f => f.Code == "connector-account-skipped" && f.Title.Contains("Rainy Day Savings"));
        _session.Imports.ReviewItems().Should().ContainSingle(r => r.Code == "connector-error").Which.Title.Should().Contain("Household Checking");
    }

    [Fact]
    public void A_card_balance_and_charges_are_turned_into_what_is_owed()
    {
        var card = new ConnectorAccount("card-9", "Rewards Card", AccountKind.CreditCard, Currency.Usd);
        _linked[card.ExternalId] = new Account(new AccountId(Guid.CreateVersion7()), "Rewards Card", AccountKind.CreditCard, Currency.Usd);
        var sync = FakeBank.First(Morning) with
        {
            Accounts = [FakeBank.Checking, FakeBank.Savings, card],
            Balances = [new(card.ExternalId, "bal-9", Money.Usd(-311.42m), new DateOnly(2026, 10, 8))],
            Transactions = [new(card.ExternalId, "c-1", new DateOnly(2026, 10, 7), Money.Usd(-25.10m), false, "Green Grocer")],
        };

        Apply(sync);

        var id = _linked[card.ExternalId].Id;
        _session.Data.Balances(id).Single().Balance.Should().Be(Money.Usd(311.42m));
        _session.Data.Transactions(id).Single().Amount.Should().Be(Money.Usd(25.10m));
    }

    [Fact]
    public void Data_for_an_unlisted_account_blocks_the_sync()
    {
        var sync = FakeBank.First(Morning) with { Accounts = [FakeBank.Savings] };

        var plan = _imports.Plan(_session, ConnectorBatches.From(sync, FakeBank.DisplayName, Today, Currency.Usd, _linked));

        plan.CanCommit.Should().BeFalse();
        plan.Findings.Should().Contain(f => f.Code == "connector-unknown-account");
    }

    [Fact]
    public void A_connection_and_its_credential_stay_inside_the_profile_and_out_of_exports()
    {
        const string Secret = "https://user:s3cret-token@bridge.example/simplefin";
        var connection = new Connection(Guid.CreateVersion7(), "simplefin", Secret, null,
            new Dictionary<string, Guid> { ["ACT-1"] = _linked[FakeBank.Checking.ExternalId].Id.Value }, Morning);

        _session.Imports.SaveConnection(connection);
        _session.Imports.SaveConnection(connection with { Cursor = "1760270400", LastSyncAt = Morning.AddHours(1) });

        var stored = _session.Imports.Connections().Should().ContainSingle().Subject;
        stored.Secret.Should().Be(Secret);
        stored.Cursor.Should().Be("1760270400");
        stored.Links.Should().BeEquivalentTo(connection.Links);
        stored.LastSyncAt.Should().Be(Morning.AddHours(1));
        Mossmint.Application.Export.ProfileExport.Files(_session.Data, "Juniper", Morning)
            .Should().NotContain(f => f.Content.Contains("s3cret", StringComparison.Ordinal));

        _session.Imports.RemoveConnection(connection.Id);

        _session.Imports.Connections().Should().BeEmpty();
    }

    private ImportPlan Apply(ConnectorSync sync)
    {
        var plan = _imports.Plan(_session, ConnectorBatches.From(sync, FakeBank.DisplayName, Today, Currency.Usd, _linked));
        ImportService.Commit(_session, plan);
        return plan;
    }
}
