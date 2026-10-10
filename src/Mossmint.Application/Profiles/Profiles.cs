using System.Globalization;
using Mossmint.Application.Data;
using Mossmint.Application.Import;

namespace Mossmint.Application.Profiles;

public readonly record struct ProfileId(Guid Value)
{
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

// What can be shown before a profile is unlocked: its name, never its data.
public sealed record ProfileSummary(ProfileId Id, string Name, DateTimeOffset CreatedAt, bool UnlocksOnThisDevice);

public abstract record UnlockSecret
{
    public sealed record Passphrase(string Value) : UnlockSecret;

    public sealed record RecoveryKey(string Value) : UnlockSecret;

    public sealed record ThisDevice : UnlockSecret;
}

public enum UnlockFailure
{
    WrongSecret,
    MalformedRecoveryKey,
    NotSetUpOnThisDevice,
    ProfileMissing,
    ProfileDamaged,
    EncryptionUnavailable,

    // The profile was saved by a newer Mossmint; this build doesn't know its schema.
    NewerVersion,
}

public abstract record UnlockResult
{
    public sealed record Unlocked(IProfileSession Session) : UnlockResult;

    public sealed record Failed(UnlockFailure Reason, string Message) : UnlockResult;
}

// The recovery key is returned exactly once, at creation; Mossmint never stores it.
public sealed record CreatedProfile(IProfileSession Session, string RecoveryKey);

public interface IProfileStore
{
    IReadOnlyList<ProfileSummary> List();

    CreatedProfile Create(string name, string passphrase, bool unlockOnThisDevice);

    UnlockResult Unlock(ProfileId id, UnlockSecret secret);

    // Removes the profile's folder: database, keys and local snapshots.
    void Delete(ProfileId id);
}

// An unlocked profile. Disposing locks it again.
public interface IProfileSession : IDisposable
{
    ProfileSummary Profile { get; }

    IProfileData Data { get; }

    IImportStore Imports { get; }

    void ChangePassphrase(string newPassphrase);

    // Replaces the recovery key and returns the new one; the old one stops working.
    string ReplaceRecoveryKey();

    void SetUnlockOnThisDevice(bool enabled);
}

public static class PassphrasePolicy
{
    public const int MinimumLength = 12;

    // Length is counted in user-perceived characters, so accents and emoji count once.
    public static string? Problem(string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return "Enter a passphrase.";
        }

        var length = new StringInfo(passphrase).LengthInTextElements;
        return length < MinimumLength
            ? $"Use at least {MinimumLength} characters; a few unrelated words work well."
            : null;
    }
}

public sealed class InvalidPassphraseException(string message) : ArgumentException(message);
