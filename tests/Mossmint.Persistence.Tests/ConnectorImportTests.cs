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
        var checking = _session.Data.Accounts().Single(a => a.Name == "Everyday Checking");
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

        plan.Changes.Should().ContainSingle(c => c.Key.EndsWith("|t-101", StringComparison.Ordinal)).Which.Kind.Should().Be(ChangeKind.Update);
        _session.Imports.Transactions().Single(t => t.Key.EndsWith("|t-101", StringComparison.Ordinal))
            .Transaction.Status.Should().Be(TransactionStatus.Posted);
        _session.Imports.Transactions().Should().HaveCount(4);
    }

    [Fact]
    public void A_removed_transaction_is_flagged_and_kept_never_deleted()
    {
        Apply(FakeBank.First(Morning));

        var plan = Apply(FakeBank.Second(Morning.AddDays(1)));

        plan.Changes.Should().ContainSingle(c => c.Kind == ChangeKind.RemovedAtSource);
        var removed = _session.Imports.Transactions().Single(t => t.Key.EndsWith("|t-102", StringComparison.Ordinal));
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

        var checking = _session.Data.Accounts().Single(a => a.Name == "Everyday Checking");
        var balances = _session.Data.Balances(checking.Id);
        balances.Should().HaveCount(2);
        balances[0].Balance.Should().Be(Money.Usd(1_409.85m), "the newest observation stays current");
    }

    [Fact]
    public void An_account_dropped_from_a_sync_is_kept_and_queued()
    {
        Apply(FakeBank.First(Morning));

        var plan = Apply(FakeBank.Second(Morning.AddDays(1)) with { Accounts = [FakeBank.Checking] });

        plan.Changes.Should().ContainSingle(c => c.Kind == ChangeKind.NotInSource).Which.Key.Should().EndWith(":sav-002");
        _session.Data.Accounts().Should().Contain(a => a.Name == "Rainy Day Savings");
    }

    [Fact]
    public void Data_for_an_unlisted_account_blocks_the_sync()
    {
        var sync = FakeBank.First(Morning) with { Accounts = [FakeBank.Savings] };

        var plan = _imports.Plan(_session, ConnectorBatches.From(sync, FakeBank.DisplayName, Today, Currency.Usd));

        plan.CanCommit.Should().BeFalse();
        plan.Findings.Should().Contain(f => f.Code == "connector-unknown-account");
    }

    private ImportPlan Apply(ConnectorSync sync)
    {
        var plan = _imports.Plan(_session, ConnectorBatches.From(sync, FakeBank.DisplayName, Today, Currency.Usd));
        ImportService.Commit(_session, plan);
        return plan;
    }
}
