using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mossmint.Application.Backups;
using Mossmint.Application.Data;
using Mossmint.Application.Profiles;
using Mossmint.Persistence.Data;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Backups;

// What a backup claims to contain, checked on every restore.
internal sealed record BackupManifest(
    Guid ProfileId,
    string ProfileName,
    DateTimeOffset ProfileCreatedAt,
    DateTimeOffset BackedUpAt,
    string? LastMigration,
    ProfileCounts Counts,
    string DatabaseSha256);

public sealed class BackupService(ProfileLocations locations, TimeProvider clock) : IBackupService
{
    public const string Extension = ".mmbak";
    private const string ManifestEntry = "backup.json";

    public BackupReport Create(IProfileSession session, string path)
    {
        var profile = (ProfileSession)session;
        locations.EnsureCreated();
        var staging = locations.StagingDirectory("backup");
        Directory.CreateDirectory(staging);
        var partial = path + ".partial";
        try
        {
            var database = Path.Combine(staging, ProfileManifest.DatabaseFileName);
            using (var snapshot = EncryptedSqlite.Open(database, profile.Key, create: true))
            {
                profile.Connection.BackupDatabase(snapshot);
                // A single self-contained file: fold the WAL back into the database.
                EncryptedSqlite.Execute(snapshot, "PRAGMA journal_mode = DELETE");
            }

            var counts = session.Data.Counts();
            var manifest = new BackupManifest(
                profile.Manifest.Id, profile.Manifest.Name, profile.Manifest.CreatedAt, clock.GetUtcNow(),
                LastMigration(profile.Connection), counts, Sha256(database));
            var keys = profile.Manifest.WithoutEnvelope(EnvelopeKind.ThisDevice);

            var payload = Path.Combine(staging, "payload.zip");
            using (var zip = ZipFile.Open(payload, ZipArchiveMode.Create))
            {
                AddEntry(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest));
                AddEntry(zip, ProfileManifest.FileName, JsonSerializer.SerializeToUtf8Bytes(keys));
                zip.CreateEntryFromFile(database, ProfileManifest.DatabaseFileName, CompressionLevel.NoCompression);
            }

            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            using (var input = File.OpenRead(payload))
            {
                BackupContainer.Write(output, [.. keys.Envelopes], profile.Key, input);
            }

            // A backup only counts once it has been restored somewhere and matches.
            var verified = Verify(partial, profile.Key);
            if (verified != counts)
            {
                throw new InvalidDataException("The backup was written but didn't verify; it has been discarded.");
            }

            File.Move(partial, path, overwrite: true);
            return new BackupReport(path, new FileInfo(path).Length, manifest.BackedUpAt, counts);
        }
        finally
        {
            File.Delete(partial);
            TryDelete(staging);
        }
    }

    // Keeps the newest `keep` backups in the profile's own folder. These protect
    // against mistakes and failed upgrades, not against losing the PC.
    public BackupReport CreateLocal(IProfileSession session, int keep = 5)
    {
        var folder = Path.Combine(((ProfileSession)session).Folder, "backups");
        Directory.CreateDirectory(folder);
        var name = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + Extension;
        var report = Create(session, Path.Combine(folder, name));
        foreach (var old in Directory.GetFiles(folder, "*" + Extension).Order(StringComparer.Ordinal).SkipLast(keep))
        {
            File.Delete(old);
        }

        return report;
    }

    public RestoreResult Restore(string path, UnlockSecret secret)
    {
        locations.EnsureCreated();
        BackupHeader header;
        byte[] headerBytes;
        DataKey key;
        try
        {
            using (var stream = File.OpenRead(path))
            {
                header = BackupContainer.ReadHeader(stream, out headerBytes);
            }

            key = Unwrap(header, secret);
        }
        catch (BackupFormatException e)
        {
            return new RestoreResult.Failed(e.UnsupportedVersion ? RestoreFailure.UnsupportedVersion : RestoreFailure.NotABackup, e.Message);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return new RestoreResult.Failed(RestoreFailure.NotABackup, "The backup file can't be opened.");
        }
        catch (WrongSecretException)
        {
            return new RestoreResult.Failed(RestoreFailure.WrongSecret, "That passphrase or recovery key doesn't open this backup. Use the ones that were current when it was made.");
        }
        catch (FormatException)
        {
            return new RestoreResult.Failed(RestoreFailure.MalformedRecoveryKey, "A recovery key is 32 letters and digits, like ABCD-EFGH-….");
        }
        catch (NotSupportedException e)
        {
            return new RestoreResult.Failed(RestoreFailure.ThisDeviceCantRestore, e.Message);
        }

        var staging = locations.StagingDirectory("restore");
        Directory.CreateDirectory(staging);
        using (key)
        {
            try
            {
                var (manifest, keys) = Extract(path, header, headerBytes, key, staging);

                // Restoring never replaces a profile: a backup of a profile that still
                // exists here comes back as a separate copy with its own id.
                var id = manifest.ProfileId;
                if (Directory.Exists(locations.ProfileDirectory(new ProfileId(id))))
                {
                    id = Guid.CreateVersion7(clock.GetUtcNow());
                    keys = keys with { Id = id };
                }

                keys.Write(staging);
                ProfileCounts counts;
                using (var connection = ProfileMigrator.OpenAndMigrate(staging, key, clock, isNew: false))
                using (var db = ProfileDbContext.For(connection))
                {
                    counts = new ProfileStoreData(db, clock).Counts();
                }

                SqliteConnection.ClearAllPools();
                if (counts != manifest.Counts)
                {
                    return new RestoreResult.Failed(RestoreFailure.Damaged, "The restored profile doesn't match what the backup recorded.");
                }

                var final = locations.ProfileDirectory(new ProfileId(id));
                Directory.Move(staging, final);
                return new RestoreResult.Restored(ProfileStore.Summary(keys), counts);
            }
            catch (ProfileFromNewerVersionException)
            {
                return new RestoreResult.Failed(RestoreFailure.NewerVersion,
                    "This backup comes from a newer version of Mossmint. Update Mossmint, then restore it.");
            }
            catch (Exception e) when (e is BackupFormatException or InvalidDataException or SqliteException or WrongSecretException or JsonException)
            {
                return new RestoreResult.Failed(RestoreFailure.Damaged, e.Message);
            }
            finally
            {
                TryDelete(staging);
            }
        }
    }

    // Test-restores into a scratch folder with the data key and returns the counts.
    private ProfileCounts Verify(string path, DataKey key)
    {
        var scratch = locations.StagingDirectory("verify");
        Directory.CreateDirectory(scratch);
        try
        {
            using var stream = File.OpenRead(path);
            var header = BackupContainer.ReadHeader(stream, out var headerBytes);
            var (manifest, _) = Extract(path, header, headerBytes, key, scratch);
            using var connection = EncryptedSqlite.Open(Path.Combine(scratch, ProfileManifest.DatabaseFileName), key);
            using var db = ProfileDbContext.For(connection);
            var counts = new ProfileStoreData(db, clock).Counts();
            return counts == manifest.Counts ? counts : throw new InvalidDataException("The backup's contents don't match its manifest.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(scratch);
        }
    }

    // Decrypts and unpacks a backup, then checks the database hash and SQLite's
    // own integrity check before anything is trusted.
    private static (BackupManifest Manifest, ProfileManifest Keys) Extract(string path, BackupHeader header, byte[] headerBytes, DataKey key, string target)
    {
        var payload = Path.Combine(target, "payload.zip");
        using (var input = File.OpenRead(path))
        using (var output = File.Create(payload))
        {
            BackupContainer.ReadHeader(input, out _);
            BackupContainer.ReadPayload(input, header, headerBytes, key, output);
        }

        BackupManifest manifest;
        ProfileManifest keys;
        using (var zip = ZipFile.OpenRead(payload))
        {
            manifest = Read<BackupManifest>(zip, ManifestEntry);
            keys = Read<ProfileManifest>(zip, ProfileManifest.FileName);
            var entry = zip.GetEntry(ProfileManifest.DatabaseFileName) ?? throw new InvalidDataException("The backup has no database.");
            entry.ExtractToFile(Path.Combine(target, ProfileManifest.DatabaseFileName));
        }

        File.Delete(payload);
        var database = Path.Combine(target, ProfileManifest.DatabaseFileName);
        if (Sha256(database) != manifest.DatabaseSha256)
        {
            throw new InvalidDataException("The backup's database doesn't match its fingerprint.");
        }

        using (var connection = EncryptedSqlite.Open(database, key))
        {
            if (EncryptedSqlite.Scalar(connection, "PRAGMA integrity_check") is not "ok")
            {
                throw new InvalidDataException("The backup's database failed SQLite's integrity check.");
            }
        }

        SqliteConnection.ClearAllPools();
        return (manifest, keys);
    }

    private static DataKey Unwrap(BackupHeader header, UnlockSecret secret)
    {
        KeyEnvelope Find(EnvelopeKind kind) =>
            header.Envelopes.FirstOrDefault(e => e.Kind == kind) ?? throw new BackupFormatException($"The backup has no {kind} key.");

        return secret switch
        {
            UnlockSecret.Passphrase(var passphrase) => KeyEnvelopes.UnwrapWithPassphrase(Find(EnvelopeKind.Passphrase), passphrase),
            UnlockSecret.RecoveryKey(var text) => RecoveryKey.TryParse(text, out var recovery)
                ? KeyEnvelopes.UnwrapWithRecoveryKey(Find(EnvelopeKind.RecoveryKey), recovery!)
                : throw new FormatException(),
            _ => throw new NotSupportedException("A backup needs the passphrase or recovery key; this PC's unlock doesn't travel with it."),
        };
    }

    private static string? LastMigration(SqliteConnection connection)
    {
        using var db = ProfileDbContext.For(connection);
        return db.Database.GetAppliedMigrations().LastOrDefault();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }

    private static T Read<T>(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"The backup has no {name}.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException($"The backup's {name} is empty.");
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Staging folders start with a dot, so a leftover is never listed as a profile.
        }
    }
}
