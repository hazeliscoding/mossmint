using Microsoft.Data.Sqlite;
using Mossmint.Application.Backups;
using Mossmint.Application.Data;
using Mossmint.Application.Profiles;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;

namespace Mossmint.Persistence.Diagnostics;

public sealed record UpgradeReport(bool Passed, string? Failure, ProfileCounts? Upgraded, ProfileCounts? Restored);

// The release workflow's upgrade test, run by the installed build: the one profile
// under dataRoot, made by the previous release, must unlock (which brings its schema
// up to date), and its backup must restore into a throwaway root with the same
// records. The report holds counts only, never values.
public static class UpgradeCheck
{
    public static UpgradeReport Run(string dataRoot, string passphrase, string backupPath, TimeProvider clock)
    {
        var secret = new UnlockSecret.Passphrase(passphrase);
        var store = new ProfileStore(new ProfileLocations(dataRoot), clock);
        if (store.List() is not [var profile])
        {
            return new(false, "Expected exactly one profile in the data root.", null, null);
        }

        ProfileCounts upgraded;
        switch (store.Unlock(profile.Id, secret))
        {
            case UnlockResult.Unlocked { Session: var session }:
                using (session)
                {
                    upgraded = session.Data.Counts();
                }

                break;
            case UnlockResult.Failed failed:
                return new(false, $"The profile didn't open: {failed.Message}", null, null);
            default:
                return new(false, "The profile didn't open.", null, null);
        }

        var restoreRoot = Path.Combine(Path.GetTempPath(), "mossmint-upgrade-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreRoot);
        try
        {
            return new BackupService(new ProfileLocations(restoreRoot), clock).Restore(backupPath, secret) switch
            {
                RestoreResult.Restored { Counts: var restored } when restored == upgraded => new(true, null, upgraded, restored),
                RestoreResult.Restored { Counts: var restored } => new(false, "The upgraded profile and its restored backup don't match.", upgraded, restored),
                RestoreResult.Failed failed => new(false, $"The backup didn't restore: {failed.Message}", upgraded, null),
                _ => new(false, "The backup didn't restore.", upgraded, null),
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(restoreRoot, recursive: true);
            }
            catch (IOException)
            {
                // Left for the OS temp cleanup; it holds only the synthetic test profile.
            }
        }
    }
}
