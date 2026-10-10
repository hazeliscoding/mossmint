using System.Text;
using Microsoft.Data.Sqlite;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Persistence.Profiles;
using Mossmint.Persistence.Security;
using Mossmint.Persistence.Sqlite;

namespace Mossmint.Persistence.Tests;

public class ProfileStoreTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static Account Checking(string name = "Canary Checking 7f3a") =>
        new(new AccountId(Guid.NewGuid()), name, AccountKind.Checking, Currency.Usd);

    [Fact]
    public void A_new_profile_is_encrypted_on_disk()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, unlockOnThisDevice: false);
        using (created.Session)
        {
            var account = Checking();
            created.Session.Data.AddAccount(account, new BalanceSnapshot(account.Id, Money.Usd(1_240m), Provenance.UserConfirmed(Today)));
        }

        var folder = _root.Locations.ProfileDirectory(created.Session.Profile.Id);
        foreach (var file in Directory.GetFiles(folder, "profile.db*"))
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            text.Should().NotStartWith("SQLite format 3", because: $"{Path.GetFileName(file)} must not be plain SQLite");
            text.Should().NotContain("Canary Checking", because: $"{Path.GetFileName(file)} must not hold plaintext");
        }
    }

    [Fact]
    public void Without_the_key_the_database_doesnt_open()
    {
        var created = _root.Store().Create("Juniper", Passphrase, false);
        var path = Path.Combine(_root.Locations.ProfileDirectory(created.Session.Profile.Id), ProfileManifest.DatabaseFileName);
        created.Session.Dispose();

        using var unkeyed = new SqliteConnection($"Data Source={path};Pooling=False");
        unkeyed.Open();
        var read = () => EncryptedSqlite.Scalar(unkeyed, "SELECT count(*) FROM sqlite_master");
        var wrongKey = () => EncryptedSqlite.Open(path, DataKey.Generate());

        read.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26);
        wrongKey.Should().Throw<WrongSecretException>();
    }

    [Fact]
    public void The_key_file_holds_no_secret_in_clear()
    {
        var created = _root.Store().Create("Juniper", Passphrase, false);
        created.Session.Dispose();

        var json = File.ReadAllText(Path.Combine(_root.Locations.ProfileDirectory(created.Session.Profile.Id), ProfileManifest.FileName));

        json.Should().Contain("Juniper");
        json.Should().NotContain(Passphrase).And.NotContain(created.RecoveryKey).And.NotContain(created.RecoveryKey.Replace("-", ""));
    }

    [Fact]
    public void Each_secret_unlocks_and_wrong_ones_fail_cleanly()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, unlockOnThisDevice: OperatingSystem.IsWindows());
        var id = created.Session.Profile.Id;
        created.Session.Dispose();

        Unlocks(store.Unlock(id, new UnlockSecret.Passphrase(Passphrase))).Dispose();
        Unlocks(store.Unlock(id, new UnlockSecret.RecoveryKey(created.RecoveryKey.ToLowerInvariant()))).Dispose();
        if (OperatingSystem.IsWindows())
        {
            Unlocks(store.Unlock(id, new UnlockSecret.ThisDevice())).Dispose();
        }

        Fails(store.Unlock(id, new UnlockSecret.Passphrase("moss under the old elm")), UnlockFailure.WrongSecret);
        Fails(store.Unlock(id, new UnlockSecret.RecoveryKey("not-a-key")), UnlockFailure.MalformedRecoveryKey);
        Fails(store.Unlock(new ProfileId(Guid.NewGuid()), new UnlockSecret.Passphrase(Passphrase)), UnlockFailure.ProfileMissing);
    }

    [Fact]
    public void Device_unlock_works_only_once_enabled()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, unlockOnThisDevice: false);
        var id = created.Session.Profile.Id;

        Fails(store.Unlock(id, new UnlockSecret.ThisDevice()), UnlockFailure.NotSetUpOnThisDevice);
        if (!OperatingSystem.IsWindows())
        {
            created.Session.Dispose();
            return;
        }

        created.Session.SetUnlockOnThisDevice(true);
        created.Session.Profile.UnlocksOnThisDevice.Should().BeTrue();
        created.Session.Dispose();
        Unlocks(store.Unlock(id, new UnlockSecret.ThisDevice())).Dispose();
        store.List().Single().UnlocksOnThisDevice.Should().BeTrue();
    }

    [Fact]
    public void Changing_the_passphrase_retires_the_old_one_and_keeps_the_data()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, false);
        var id = created.Session.Profile.Id;
        created.Session.Data.AddAccount(Checking("Everyday"));
        created.Session.ChangePassphrase("a brand new passphrase here");
        created.Session.Dispose();

        Fails(store.Unlock(id, new UnlockSecret.Passphrase(Passphrase)), UnlockFailure.WrongSecret);
        using (var session = Unlocks(store.Unlock(id, new UnlockSecret.Passphrase("a brand new passphrase here"))))
        {
            session.Data.Accounts().Single().Name.Should().Be("Everyday");
        }

        Unlocks(store.Unlock(id, new UnlockSecret.RecoveryKey(created.RecoveryKey))).Dispose();
    }

    [Fact]
    public void A_replaced_recovery_key_retires_the_old_one()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, false);
        var id = created.Session.Profile.Id;
        var replacement = created.Session.ReplaceRecoveryKey();
        created.Session.Dispose();

        Fails(store.Unlock(id, new UnlockSecret.RecoveryKey(created.RecoveryKey)), UnlockFailure.WrongSecret);
        Unlocks(store.Unlock(id, new UnlockSecret.RecoveryKey(replacement))).Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("           ")]
    public void Weak_passphrases_are_refused(string passphrase)
    {
        var act = () => _root.Store().Create("Juniper", passphrase, false);

        act.Should().Throw<InvalidPassphraseException>();
        _root.Store().List().Should().BeEmpty();
    }

    [Fact]
    public void Records_survive_locking_and_unlocking()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, false);
        var id = created.Session.Profile.Id;
        var checking = Checking("Everyday");
        var loanAccount = new Account(new AccountId(Guid.NewGuid()), "Car loan", AccountKind.InstallmentLoan, Currency.Usd);
        var bill = new RecurringBill(new BillId(Guid.NewGuid()), "Electric", AmountPolicy.Estimated(Money.Usd(70m), Money.Usd(60m), Money.Usd(95m)),
            new Schedule(new MonthlyRule(DayOfMonth.Of(12))), BillPurpose.Expense, Provenance.UserConfirmed(Today), checking.Id);
        var income = new IncomeSchedule(new IncomeId(Guid.NewGuid()), "Paycheck", Money.Usd(2_050m),
            new Schedule(new SemiMonthlyRule(DayOfMonth.Of(15), DayOfMonth.Last), BusinessDayAdjustment.PreviousBusinessDay),
            Provenance.UserConfirmed(Today), [new PayAmountOverride(new DateOnly(2026, 12, 15), Money.Usd(2_400m))], checking.Id);
        var debt = new DebtAccount(loanAccount.Id, new SimpleInterestLoanTerms(
            RateSchedule.Fixed(AnnualRate.FromPercent(7.4m), new DateOnly(2024, 1, 1)), DayCountConvention.Actual365Fixed, Money.Usd(285m),
            new Schedule(new MonthlyRule(DayOfMonth.Of(28)))), Money.Usd(18_000m), new DateOnly(2024, 1, 28));
        var settings = new Application.Data.PlanSettings(Currency.Usd, new SavingsContributionTarget(Money.Usd(150m)), new CashReserveFloor(Money.Usd(300m)));

        using (var s = created.Session)
        {
            s.Data.AddAccount(checking, new BalanceSnapshot(checking.Id, Money.Usd(1_240.55m), Provenance.UserConfirmed(Today)));
            s.Data.AddAccount(loanAccount, new BalanceSnapshot(loanAccount.Id, Money.Usd(9_800m), Provenance.LenderStatement(Today, "statement"), AccruedInterest: Money.Usd(12.40m)));
            s.Data.SaveBill(bill);
            s.Data.SaveIncome(income);
            s.Data.SaveDebt(debt);
            s.Data.SaveSettings(settings);
        }

        using var session = Unlocks(store.Unlock(id, new UnlockSecret.Passphrase(Passphrase)));
        var data = session.Data;
        data.Accounts().Should().Equal(checking, loanAccount);
        data.Balances(loanAccount.Id).Single().Should().Be(new BalanceSnapshot(loanAccount.Id, Money.Usd(9_800m), Provenance.LenderStatement(Today, "statement"), AccruedInterest: Money.Usd(12.40m), RecordedAt: Now));
        data.Bills().Single().Should().BeEquivalentTo(bill);
        data.Income().Single().Should().BeEquivalentTo(income);
        data.Debts().Single().Should().BeEquivalentTo(debt);
        data.Settings().Should().BeEquivalentTo(settings);
        data.Counts().Should().Be(new Application.Data.ProfileCounts(2, 2, 1, 1, 1, 8));
    }

    [Fact]
    public void Manual_entries_keep_the_bill_they_pay_and_undo_leaves_them_void()
    {
        var created = _root.Store().Create("Juniper", Passphrase, false);
        using var session = created.Session;
        var checking = Checking("Everyday");
        session.Data.AddAccount(checking);
        var paid = new PaidBill(new BillId(Guid.NewGuid()), new DateOnly(2026, 10, 12));
        var payment = new Transaction(new TransactionId(Guid.NewGuid()), checking.Id, Today, Money.Usd(-70m), TransactionStatus.Posted,
            "Electric", Provenance.UserConfirmed(Today), Pays: paid);

        session.Data.RecordTransaction(payment);
        session.Data.Transactions(checking.Id).Should().Equal(payment with { RecordedAt = Now });

        session.Data.VoidTransaction(payment.Id);
        session.Data.Transactions(checking.Id).Single().Status.Should().Be(TransactionStatus.Void);
        session.Data.Counts().Transactions.Should().Be(1);
    }

    [Fact]
    public void Savings_goals_round_trip_dated_ones_first()
    {
        var created = _root.Store().Create("Juniper", Passphrase, false);
        using var session = created.Session;
        var someday = new SavingsGoal(new GoalId(Guid.NewGuid()), "Someday", Money.Usd(500m), Money.Usd(0m), Provenance.UserConfirmed(Today));
        var car = new SavingsGoal(new GoalId(Guid.NewGuid()), "Car fund", Money.Usd(3_000m), Money.Usd(1_250.50m), Provenance.UserConfirmed(Today), new DateOnly(2027, 5, 1));

        session.Data.SaveGoal(someday);
        session.Data.SaveGoal(car);
        session.Data.SaveGoal(car with { Saved = Money.Usd(1_400m) });

        session.Data.Goals().Should().Equal(car with { Saved = Money.Usd(1_400m) }, someday);
        session.Data.Counts().Goals.Should().Be(2);
    }

    [Fact]
    public void Profiles_are_isolated_from_each_other()
    {
        var store = _root.Store();
        var first = store.Create("Juniper", Passphrase, false);
        var second = store.Create("Rowan", "another long passphrase", false);
        first.Session.Data.AddAccount(Checking("Juniper checking"));
        second.Session.Data.AddAccount(Checking("Rowan checking"));
        var firstPath = Path.Combine(_root.Locations.ProfileDirectory(first.Session.Profile.Id), ProfileManifest.DatabaseFileName);
        var secondKey = ((ProfileSession)second.Session).Key;

        var crossOpen = () => EncryptedSqlite.Open(firstPath, secondKey);

        crossOpen.Should().Throw<WrongSecretException>();
        first.Session.Data.Accounts().Select(a => a.Name).Should().Equal("Juniper checking");
        store.List().Select(p => p.Name).Should().Equal("Juniper", "Rowan");
        Fails(store.Unlock(second.Session.Profile.Id, new UnlockSecret.Passphrase(Passphrase)), UnlockFailure.WrongSecret);
        first.Session.Dispose();
        second.Session.Dispose();
    }

    [Fact]
    public void Unfinished_and_damaged_folders_are_not_listed()
    {
        var store = _root.Store();
        store.Create("Juniper", Passphrase, false).Session.Dispose();
        Directory.CreateDirectory(Path.Combine(_root.Locations.ProfilesDirectory, ".creating-abc"));
        var damaged = Path.Combine(_root.Locations.ProfilesDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, ProfileManifest.FileName), "{ not json");

        store.List().Select(p => p.Name).Should().Equal("Juniper");
        Fails(store.Unlock(new ProfileId(Guid.Parse(Path.GetFileName(damaged))), new UnlockSecret.Passphrase(Passphrase)), UnlockFailure.ProfileDamaged);
    }

    [Fact]
    public void Profiles_can_never_live_inside_the_program_folder()
    {
        var act = () => new ProfileLocations(Path.Combine(AppContext.BaseDirectory, "Mossmint"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*program folder*");
    }

    [Fact]
    public void Deleting_a_profile_removes_its_folder()
    {
        var store = _root.Store();
        var created = store.Create("Juniper", Passphrase, false);
        var id = created.Session.Profile.Id;
        created.Session.Dispose();

        store.Delete(id);

        store.List().Should().BeEmpty();
        Directory.Exists(_root.Locations.ProfileDirectory(id)).Should().BeFalse();
    }

    [Fact]
    public void Snapshots_are_encrypted_copies_that_open_with_the_same_key()
    {
        var created = _root.Store().Create("Juniper", Passphrase, false);
        var session = (ProfileSession)created.Session;
        session.Data.AddAccount(Checking("Snapshot me"));

        var snapshot = ProfileMigrator.Snapshot(session.Connection, session.Folder, session.Key, "test", new FixedClock(DateTimeOffset.UnixEpoch));

        Encoding.Latin1.GetString(File.ReadAllBytes(snapshot)).Should().NotContain("Snapshot me").And.NotStartWith("SQLite format 3");
        using var copy = EncryptedSqlite.Open(snapshot, session.Key);
        EncryptedSqlite.Scalar(copy, "SELECT Name FROM Accounts").Should().Be("Snapshot me");
        session.Dispose();
    }

    private static IProfileSession Unlocks(UnlockResult result) =>
        result.Should().BeOfType<UnlockResult.Unlocked>().Subject.Session;

    private static void Fails(UnlockResult result, UnlockFailure expected) =>
        result.Should().BeOfType<UnlockResult.Failed>().Which.Reason.Should().Be(expected);
}
