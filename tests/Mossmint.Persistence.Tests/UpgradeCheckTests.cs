using Microsoft.Data.Sqlite;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Diagnostics;

namespace Mossmint.Persistence.Tests;

// The release workflow's upgrade test: a profile and backup made by the previous
// release must open, and restore, in the installed new build with the same records.
public sealed class UpgradeCheckTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.Zero));
    private readonly string _backup;
    private readonly IProfileSession _session;

    public UpgradeCheckTests()
    {
        _session = _root.Store(_clock).Create("Upgrade", Passphrase, unlockOnThisDevice: false).Session;
        _session.Data.AddAccount(Checking("Checking"));
        _backup = Path.Combine(_root.Path, "upgrade.mmbak");
        new BackupService(_root.Locations, _clock).Create(_session, _backup);
    }

    public void Dispose()
    {
        _session.Dispose();
        _root.Dispose();
    }

    private UpgradeReport Run(string passphrase = Passphrase)
    {
        _session.Dispose();
        SqliteConnection.ClearAllPools();
        return UpgradeCheck.Run(_root.Path, passphrase, _backup, _clock);
    }

    [Fact]
    public void A_profile_and_its_backup_that_open_with_the_same_records_pass()
    {
        var report = Run();

        report.Passed.Should().BeTrue(report.Failure);
        report.Upgraded.Should().Be(report.Restored).And.Match<Application.Data.ProfileCounts>(c => c.Accounts == 1);
    }

    [Fact]
    public void A_backup_that_doesnt_match_the_profile_fails()
    {
        _session.Data.AddAccount(Checking("Added after the backup"));

        var report = Run();

        report.Passed.Should().BeFalse();
        report.Failure.Should().Contain("don't match");
    }

    [Fact]
    public void A_wrong_passphrase_fails_without_throwing()
    {
        var report = Run("not the passphrase at all");

        report.Passed.Should().BeFalse();
        report.Failure.Should().NotBeNullOrEmpty();
    }

    private static Account Checking(string name) => new(new AccountId(Guid.NewGuid()), name, AccountKind.Checking, Currency.Usd);
}
