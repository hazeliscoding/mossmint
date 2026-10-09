using Mossmint.Application.Data;
using Mossmint.Application.Profiles;

namespace Mossmint.Application.Backups;

// A backup that was written and then test-restored before being reported.
public sealed record BackupReport(string Path, long SizeBytes, DateTimeOffset CreatedAt, ProfileCounts Counts);

public enum RestoreFailure
{
    NotABackup,
    UnsupportedVersion,
    WrongSecret,
    MalformedRecoveryKey,
    ThisDeviceCantRestore,
    Damaged,
}

public abstract record RestoreResult
{
    // Always a new profile beside any existing ones; nothing is overwritten.
    public sealed record Restored(ProfileSummary Profile, ProfileCounts Counts) : RestoreResult;

    public sealed record Failed(RestoreFailure Reason, string Message) : RestoreResult;
}

public interface IBackupService
{
    // Writes an encrypted backup of the unlocked profile to path, then verifies
    // it by restoring it into a temporary folder. Throws if verification fails.
    BackupReport Create(IProfileSession session, string path);

    // Restores into a new profile. A backup opens with the passphrase or recovery
    // key that were current when it was made; "this PC" unlock never applies.
    RestoreResult Restore(string path, UnlockSecret secret);
}
