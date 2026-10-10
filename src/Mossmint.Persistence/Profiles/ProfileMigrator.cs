using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mossmint.Persistence.Data;
using Mossmint.Persistence.Security;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Profiles;

// Brings a profile's schema up to date (ADR-0001). An existing profile is
// copied to an encrypted snapshot first; if a migration fails, the snapshot is
// put back and the error rethrown, so a failed upgrade never leaves a half-migrated file.
// A profile saved by a newer build is refused before anything is written: this
// build would otherwise read tables it doesn't understand as if they were its own.
internal static class ProfileMigrator
{
    public const string SnapshotFolder = "snapshots";

    public static SqliteConnection OpenAndMigrate(string directory, DataKey key, TimeProvider clock, bool isNew)
    {
        var path = Path.Combine(directory, ProfileManifest.DatabaseFileName);
        var connection = EncryptedSqlite.Open(path, key, create: isNew);
        using var db = ProfileDbContext.For(connection);
        if (!isNew && db.Database.GetAppliedMigrations().Except(db.Database.GetMigrations()).LastOrDefault() is { } unknown)
        {
            connection.Dispose();
            throw new ProfileFromNewerVersionException(unknown);
        }

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0)
        {
            return connection;
        }

        if (isNew)
        {
            db.Database.Migrate();
            return connection;
        }

        var snapshot = Snapshot(connection, directory, key, $"pre-{pending[0]}", clock);
        try
        {
            db.Database.Migrate();
            return connection;
        }
        catch
        {
            connection.Dispose();
            RestoreSnapshot(snapshot, path);
            throw;
        }
    }

    // A consistent, encrypted copy via SQLite's online backup API, which includes
    // anything still in the WAL.
    public static string Snapshot(SqliteConnection source, string directory, DataKey key, string label, TimeProvider clock)
    {
        var folder = Path.Combine(directory, SnapshotFolder);
        Directory.CreateDirectory(folder);
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(folder, $"{stamp}-{label}.db");
        using var destination = EncryptedSqlite.Open(target, key, create: true);
        source.BackupDatabase(destination);
        EncryptedSqlite.Execute(destination, "PRAGMA wal_checkpoint(TRUNCATE)");
        return target;
    }

    private static void RestoreSnapshot(string snapshot, string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            File.Delete(path + suffix);
        }

        File.Copy(snapshot, path, overwrite: true);
    }
}

internal sealed class ProfileFromNewerVersionException(string migration)
    : Exception($"The profile has schema change {migration}, which this version of Mossmint doesn't know.");
