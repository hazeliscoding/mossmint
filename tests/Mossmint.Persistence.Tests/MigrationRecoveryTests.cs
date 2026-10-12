using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Persistence.Data;
using Mossmint.Persistence.Profiles;

namespace Mossmint.Persistence.Tests;

// A crash or power cut can stop an upgrade halfway, where no exception handler
// runs. The marker written before migrating lets the next open put the
// pre-upgrade snapshot back and upgrade again from a whole file.
public sealed class MigrationRecoveryTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    [Fact]
    public void An_upgrade_cut_short_is_undone_from_its_snapshot_on_the_next_open()
    {
        var store = _root.Store(_clock);
        var session = (ProfileSession)store.Create("Juniper", Passphrase, unlockOnThisDevice: false).Session;
        session.Data.AddAccount(Checking("Before the upgrade"));
        var snapshot = ProfileMigrator.Snapshot(session.Connection, session.Folder, session.Key, "pre-20261011205225_DebtPlan", _clock);
        // Stands in for the half-applied schema change the crash left behind.
        session.Data.AddAccount(Checking("Half upgraded"));
        var (id, folder) = (session.Profile.Id, session.Folder);
        session.Dispose();
        SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(folder, ProfileMigrator.MarkerFile), Path.GetFileName(snapshot));

        using var reopened = Unlocks(store.Unlock(id, new UnlockSecret.Passphrase(Passphrase)));

        reopened.Data.Accounts().Select(a => a.Name).Should().Equal("Before the upgrade");
        File.Exists(Path.Combine(folder, ProfileMigrator.MarkerFile)).Should().BeFalse("a finished recovery clears its marker");
    }

    [Fact]
    public void A_marker_whose_snapshot_is_gone_refuses_the_profile_and_changes_nothing()
    {
        var store = _root.Store(_clock);
        var session = store.Create("Juniper", Passphrase, unlockOnThisDevice: false).Session;
        var (id, folder) = (session.Profile.Id, ((ProfileSession)session).Folder);
        session.Dispose();
        SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(folder, ProfileMigrator.MarkerFile), "20261011T140000Z-pre-20261011205225_DebtPlan.db");
        var before = Fingerprint(folder);

        var result = store.Unlock(id, new UnlockSecret.Passphrase(Passphrase));

        result.Should().BeOfType<UnlockResult.Failed>().Which.Reason.Should().Be(UnlockFailure.ProfileDamaged);
        SqliteConnection.ClearAllPools();
        Fingerprint(folder).Should().Be(before, "without its snapshot, a half-upgraded file is left for a backup to replace");
    }

    [Fact]
    public void A_finished_upgrade_keeps_its_snapshot_and_clears_the_marker()
    {
        var store = _root.Store(_clock);
        var session = (ProfileSession)store.Create("Juniper", Passphrase, unlockOnThisDevice: false).Session;
        using (var db = ProfileDbContext.For(session.Connection))
        {
            db.GetService<IMigrator>().Migrate("20261010212302_SavingsGoals");
        }

        var (id, folder) = (session.Profile.Id, session.Folder);
        session.Dispose();
        SqliteConnection.ClearAllPools();

        using var upgraded = (ProfileSession)Unlocks(store.Unlock(id, new UnlockSecret.Passphrase(Passphrase)));

        using (var db = ProfileDbContext.For(upgraded.Connection))
        {
            db.Database.GetPendingMigrations().Should().BeEmpty();
        }

        Directory.GetFiles(Path.Combine(folder, ProfileMigrator.SnapshotFolder)).Select(Path.GetFileName)
            .Should().Equal("20261011T140000Z-pre-20261011205225_DebtPlan.db");
        File.Exists(Path.Combine(folder, ProfileMigrator.MarkerFile)).Should().BeFalse();
    }

    private static Account Checking(string name) => new(new AccountId(Guid.NewGuid()), name, AccountKind.Checking, Currency.Usd);

    private static IProfileSession Unlocks(UnlockResult result) =>
        result.Should().BeOfType<UnlockResult.Unlocked>().Subject.Session;

    private static string Fingerprint(string folder) =>
        string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(folder, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));
}
