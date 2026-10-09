using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Mossmint.Application.Data;
using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Persistence.Data;
using Mossmint.Persistence.Security;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Profiles;

public sealed class ProfileStore(ProfileLocations locations, TimeProvider clock, Argon2Settings? argon2 = null) : IProfileStore
{
    private readonly Argon2Settings _argon2 = argon2 ?? Argon2Settings.Default;

    public IReadOnlyList<ProfileSummary> List()
    {
        if (!Directory.Exists(locations.ProfilesDirectory))
        {
            return [];
        }

        var summaries = new List<ProfileSummary>();
        foreach (var directory in Directory.EnumerateDirectories(locations.ProfilesDirectory))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            try
            {
                summaries.Add(Summary(ProfileManifest.Read(directory)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // An unreadable folder isn't a profile we can offer; it is left untouched.
            }
        }

        return [.. summaries.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public CreatedProfile Create(string name, string passphrase, bool unlockOnThisDevice)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A profile needs a name.", nameof(name));
        }

        if (PassphrasePolicy.Problem(passphrase) is { } problem)
        {
            throw new InvalidPassphraseException(problem);
        }

        locations.EnsureCreated();
        var id = new ProfileId(Guid.CreateVersion7(clock.GetUtcNow()));
        var staging = locations.StagingDirectory("creating");
        Directory.CreateDirectory(staging);

        var key = DataKey.Generate();
        var recovery = RecoveryKey.Generate();
        try
        {
            var manifest = new ProfileManifest(
                ProfileManifest.FormatName, ProfileManifest.CurrentFormatVersion, id.Value, name.Trim(), clock.GetUtcNow(),
                [KeyEnvelopes.WrapWithPassphrase(key, passphrase, _argon2), KeyEnvelopes.WrapWithRecoveryKey(key, recovery)]);
            if (unlockOnThisDevice && OperatingSystem.IsWindows())
            {
                manifest = manifest.WithEnvelope(KeyEnvelopes.WrapForThisDevice(key));
            }

            using (ProfileMigrator.OpenAndMigrate(staging, key, clock, isNew: true))
            {
            }

            manifest.Write(staging);

            // The profile appears under its final name only once it is complete.
            var final = locations.ProfileDirectory(id);
            Directory.Move(staging, final);
            var connection = ProfileMigrator.OpenAndMigrate(final, key, clock, isNew: false);
            return new CreatedProfile(new ProfileSession(final, manifest, key, connection, clock, _argon2), recovery.ToString());
        }
        catch
        {
            key.Dispose();
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    public UnlockResult Unlock(ProfileId id, UnlockSecret secret)
    {
        var directory = locations.ProfileDirectory(id);
        ProfileManifest manifest;
        try
        {
            manifest = ProfileManifest.Read(directory);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new UnlockResult.Failed(UnlockFailure.ProfileMissing, "This profile no longer exists.");
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            return new UnlockResult.Failed(UnlockFailure.ProfileDamaged, $"The profile's key file can't be read: {e.Message}");
        }

        DataKey key;
        try
        {
            key = Unwrap(manifest, secret);
        }
        catch (WrongSecretException)
        {
            return secret is UnlockSecret.ThisDevice
                ? new UnlockResult.Failed(UnlockFailure.NotSetUpOnThisDevice, "This PC can't unlock the profile; enter the passphrase.")
                : new UnlockResult.Failed(UnlockFailure.WrongSecret, "That passphrase or recovery key doesn't match this profile.");
        }
        catch (FormatException)
        {
            return new UnlockResult.Failed(UnlockFailure.MalformedRecoveryKey, "A recovery key is 32 letters and digits, like ABCD-EFGH-….");
        }
        catch (MissingEnvelopeException e)
        {
            return new UnlockResult.Failed(e.Failure, e.Message);
        }
        catch (CryptographicException e)
        {
            return new UnlockResult.Failed(UnlockFailure.ProfileDamaged, e.Message);
        }

        try
        {
            var connection = ProfileMigrator.OpenAndMigrate(directory, key, clock, isNew: false);
            return new UnlockResult.Unlocked(new ProfileSession(directory, manifest, key, connection, clock, _argon2));
        }
        catch (EncryptionUnavailableException e)
        {
            key.Dispose();
            return new UnlockResult.Failed(UnlockFailure.EncryptionUnavailable, e.Message);
        }
        catch (Exception e) when (e is WrongSecretException or SqliteException or FileNotFoundException)
        {
            key.Dispose();
            return new UnlockResult.Failed(UnlockFailure.ProfileDamaged, "The profile's keys opened, but its database didn't.");
        }
    }

    public void Delete(ProfileId id)
    {
        var directory = locations.ProfileDirectory(id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static ProfileSummary Summary(ProfileManifest manifest) =>
        new(new ProfileId(manifest.Id), manifest.Name, manifest.CreatedAt, manifest.Envelope(EnvelopeKind.ThisDevice) is not null);

    private static DataKey Unwrap(ProfileManifest manifest, UnlockSecret secret)
    {
        switch (secret)
        {
            case UnlockSecret.Passphrase(var passphrase):
                return KeyEnvelopes.UnwrapWithPassphrase(Require(manifest, EnvelopeKind.Passphrase), passphrase);
            case UnlockSecret.RecoveryKey(var text):
                return RecoveryKey.TryParse(text, out var recovery)
                    ? KeyEnvelopes.UnwrapWithRecoveryKey(Require(manifest, EnvelopeKind.RecoveryKey), recovery!)
                    : throw new FormatException();
            case UnlockSecret.ThisDevice when OperatingSystem.IsWindows():
                return KeyEnvelopes.UnwrapForThisDevice(Require(manifest, EnvelopeKind.ThisDevice));
            default:
                throw new MissingEnvelopeException(UnlockFailure.NotSetUpOnThisDevice, "This PC can't unlock the profile; enter the passphrase.");
        }
    }

    private static KeyEnvelope Require(ProfileManifest manifest, EnvelopeKind kind) =>
        manifest.Envelope(kind) ?? throw new MissingEnvelopeException(
            kind == EnvelopeKind.ThisDevice ? UnlockFailure.NotSetUpOnThisDevice : UnlockFailure.ProfileDamaged,
            kind == EnvelopeKind.ThisDevice ? "This PC isn't set up to unlock the profile; enter the passphrase." : $"The profile has no {kind} key.");

    private sealed class MissingEnvelopeException(UnlockFailure failure, string message) : Exception(message)
    {
        public UnlockFailure Failure { get; } = failure;
    }
}

internal sealed class ProfileSession : IProfileSession
{
    private readonly string _directory;
    private readonly DataKey _key;
    private readonly SqliteConnection _connection;
    private readonly ProfileDbContext _db;
    private readonly Argon2Settings _argon2;
    private ProfileManifest _manifest;

    public ProfileSession(string directory, ProfileManifest manifest, DataKey key, SqliteConnection connection, TimeProvider clock, Argon2Settings argon2)
    {
        (_directory, _manifest, _key, _connection, _argon2) = (directory, manifest, key, connection, argon2);
        _db = ProfileDbContext.For(connection);
        var store = new ProfileStoreData(_db, clock, label => TakeSnapshot(label, clock));
        Data = store;
        Imports = store;
    }

    public ProfileSummary Profile => ProfileStore.Summary(_manifest);

    public IProfileData Data { get; }

    public IImportStore Imports { get; }

    internal string Folder => _directory;

    internal DataKey Key => _key;

    internal SqliteConnection Connection => _connection;

    internal ProfileManifest Manifest => _manifest;

    public void ChangePassphrase(string newPassphrase)
    {
        if (PassphrasePolicy.Problem(newPassphrase) is { } problem)
        {
            throw new InvalidPassphraseException(problem);
        }

        Save(_manifest.WithEnvelope(KeyEnvelopes.WrapWithPassphrase(_key, newPassphrase, _argon2)));
    }

    public string ReplaceRecoveryKey()
    {
        var recovery = RecoveryKey.Generate();
        Save(_manifest.WithEnvelope(KeyEnvelopes.WrapWithRecoveryKey(_key, recovery)));
        return recovery.ToString();
    }

    public void SetUnlockOnThisDevice(bool enabled)
    {
        if (!enabled)
        {
            Save(_manifest.WithoutEnvelope(EnvelopeKind.ThisDevice));
        }
        else if (OperatingSystem.IsWindows())
        {
            Save(_manifest.WithEnvelope(KeyEnvelopes.WrapForThisDevice(_key)));
        }
        else
        {
            throw new PlatformNotSupportedException("Unlocking with this device needs Windows.");
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _key.Dispose();
    }

    // An encrypted copy before an import; only the newest few are kept.
    private string TakeSnapshot(string label, TimeProvider clock)
    {
        var path = ProfileMigrator.Snapshot(_connection, _directory, _key, label, clock);
        var older = Directory.GetFiles(Path.GetDirectoryName(path)!, $"*-{label}.db")
            .OrderDescending(StringComparer.Ordinal)
            .Skip(ProfileStoreData.ImportSnapshotsKept);
        foreach (var file in older)
        {
            File.Delete(file);
        }

        return path;
    }

    private void Save(ProfileManifest manifest)
    {
        manifest.Write(_directory);
        _manifest = manifest;
    }
}
