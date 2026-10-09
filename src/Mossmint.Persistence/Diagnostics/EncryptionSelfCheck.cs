using System.Text;
using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Diagnostics;

public sealed record SelfCheckResult(string Check, bool Passed, string Detail);

public sealed record SelfCheckReport(bool Passed, string? CipherVersion, IReadOnlyList<SelfCheckResult> Checks);

// Proves, inside whatever build is running (a packaged install included), that
// profiles really are encrypted: the cipher library loaded, a throwaway profile
// shows no plaintext on disk, a wrong key is refused, only one SQLite binary
// ships, and a backup round-trips. It touches only a temporary folder.
public static class EncryptionSelfCheck
{
    private const string Canary = "SELF-CHECK-CANARY-4f1b";
    private const string Passphrase = "self check passphrase";

    public static SelfCheckReport Run(string? appDirectory = null)
    {
        var results = new List<SelfCheckResult>();
        string? cipherVersion = null;
        var root = Path.Combine(Path.GetTempPath(), "mossmint-self-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(results, "cipher library loaded", () =>
            {
                EncryptedSqlite.EnsureCipherBuild();
                return "sqlite3mc";
            });

            var locations = new ProfileLocations(root, appDirectory);
            var store = new ProfileStore(locations, TimeProvider.System, new Argon2Settings(8 * 1024, 1, 1));
            var created = store.Create("Self check", Passphrase, false);
            var id = created.Session.Profile.Id;
            using (var session = created.Session)
            {
                var account = new Account(new AccountId(Guid.NewGuid()), Canary, AccountKind.Checking, Currency.Usd);
                session.Data.AddAccount(account, new BalanceSnapshot(account.Id, Money.Usd(1m), Provenance.UserConfirmed(DateOnly.FromDateTime(DateTime.Today))));
                cipherVersion = EncryptedSqlite.CipherVersion(((ProfileSession)session).Connection);
            }

            var folder = locations.ProfileDirectory(id);
            Check(results, "no plaintext on disk", () =>
            {
                foreach (var file in Directory.GetFiles(folder, "profile.db*"))
                {
                    var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
                    if (text.StartsWith("SQLite format 3", StringComparison.Ordinal) || text.Contains(Canary, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"{Path.GetFileName(file)} holds plaintext.");
                    }
                }

                return "database and journal are ciphertext";
            });

            Check(results, "wrong key refused", () =>
            {
                try
                {
                    using var _ = EncryptedSqlite.Open(Path.Combine(folder, ProfileManifest.DatabaseFileName), DataKey.Generate());
                }
                catch (WrongSecretException)
                {
                    return "a random key can't open the profile";
                }

                throw new InvalidOperationException("A random key opened the profile.");
            });

            Check(results, "one SQLite binary", () =>
            {
                var directory = appDirectory ?? AppContext.BaseDirectory;
                var natives = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .Where(name => name!.Contains("sqlite", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith("SQLite3MC.PCLRaw.", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return natives.SequenceEqual(["sqlite3mc.dll"], StringComparer.OrdinalIgnoreCase)
                    ? "sqlite3mc.dll only"
                    : throw new InvalidOperationException($"Native SQLite binaries found: {string.Join(", ", natives)}");
            });

            Check(results, "backup round-trip", () =>
            {
                var backups = new BackupService(locations, TimeProvider.System);
                var path = Path.Combine(root, "self-check.mmbak");
                var unlocked = (UnlockResult.Unlocked)store.Unlock(id, new UnlockSecret.Passphrase(Passphrase));
                using (var session = unlocked.Session)
                {
                    backups.Create(session, path);
                }

                return backups.Restore(path, new UnlockSecret.Passphrase(Passphrase)) is RestoreResult.Restored restored
                    ? $"restored {restored.Counts.Accounts} account(s)"
                    : throw new InvalidOperationException("The backup didn't restore.");
            });
        }
        catch (Exception e)
        {
            results.Add(new SelfCheckResult("setup", false, e.Message));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Temporary files only; the OS cleans them up.
            }
        }

        return new SelfCheckReport(results.Count > 0 && results.All(r => r.Passed), cipherVersion, results);
    }

    private static void Check(List<SelfCheckResult> results, string name, Func<string> check)
    {
        try
        {
            results.Add(new SelfCheckResult(name, true, check()));
        }
        catch (Exception e)
        {
            results.Add(new SelfCheckResult(name, false, e.Message));
        }
    }
}
