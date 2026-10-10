using System.Security.Cryptography;
using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Tests;

// An older Mossmint must never open, or restore, a profile a newer one has
// changed: it would read tables it doesn't understand as if they were its own.
// The positive controls are the other tests, which open and restore current profiles.
public sealed class NewerVersionTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_profile_from_a_newer_version_is_refused_and_left_untouched()
    {
        var store = _root.Store(_clock);
        var session = FromTheFuture(store);
        var id = session.Profile.Id;
        session.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var folder = _root.Locations.ProfileDirectory(id);
        var before = Fingerprint(folder);

        var result = store.Unlock(id, new UnlockSecret.Passphrase(Passphrase));

        result.Should().BeOfType<UnlockResult.Failed>().Which.Reason.Should().Be(UnlockFailure.NewerVersion);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Fingerprint(folder).Should().Be(before, "a refused profile isn't migrated, snapshotted or rewritten");
    }

    [Fact]
    public void A_backup_from_a_newer_version_is_refused_and_restores_nothing()
    {
        var store = _root.Store(_clock);
        var backups = new BackupService(_root.Locations, _clock);
        var path = Path.Combine(_root.Path, "future.mmbak");
        using (var session = FromTheFuture(store))
        {
            backups.Create(session, path);
        }

        var result = backups.Restore(path, new UnlockSecret.Passphrase(Passphrase));

        result.Should().BeOfType<RestoreResult.Failed>().Which.Reason.Should().Be(RestoreFailure.NewerVersion);
        store.List().Should().ContainSingle("nothing was restored next to the original");
    }

    // An open profile whose history records a schema change this build has never heard of.
    private static IProfileSession FromTheFuture(ProfileStore store)
    {
        var session = store.Create("Juniper", Passphrase, unlockOnThisDevice: false).Session;
        EncryptedSqlite.Execute(((ProfileSession)session).Connection,
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_FromTheFuture', '99.0.0')");
        return session;
    }

    private static string Fingerprint(string folder) =>
        string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(folder, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}"));
}
