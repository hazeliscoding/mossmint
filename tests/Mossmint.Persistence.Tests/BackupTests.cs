using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mossmint.Application.Backups;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Persistence.Backups;
using Mossmint.Persistence.Security;

namespace Mossmint.Persistence.Tests;

public class BackupTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private static readonly DateOnly Today = new(2026, 10, 9);
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private BackupService Backups() => new(_root.Locations, new FixedClock(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero)));

    private string BackupPath(string name = "juniper.mmbak") => Path.Combine(_root.Path, name);

    // A profile with one of everything, so counts and records are meaningful.
    private (CreatedProfile Created, Account Checking) Seeded()
    {
        var created = _root.Store().Create("Juniper", Passphrase, unlockOnThisDevice: OperatingSystem.IsWindows());
        var checking = new Account(new AccountId(Guid.NewGuid()), "Canary Checking 7f3a", AccountKind.Checking, Currency.Usd);
        created.Session.Data.AddAccount(checking, new BalanceSnapshot(checking.Id, Money.Usd(1_240.55m), Provenance.UserConfirmed(Today)));
        created.Session.Data.SaveBill(new RecurringBill(new BillId(Guid.NewGuid()), "Canary Rent", AmountPolicy.Fixed(Money.Usd(1_150m)),
            new Schedule(new MonthlyRule(DayOfMonth.Of(1))), BillPurpose.Expense, Provenance.UserConfirmed(Today), checking.Id));
        return (created, checking);
    }

    [Fact]
    public void A_backup_restores_as_a_separate_copy_with_identical_records()
    {
        var (created, checking) = Seeded();
        var report = Backups().Create(created.Session, BackupPath());
        var originalId = created.Session.Profile.Id;
        var originalBills = created.Session.Data.Bills();
        created.Session.Dispose();

        var restored = Backups().Restore(report.Path, new UnlockSecret.Passphrase(Passphrase))
            .Should().BeOfType<RestoreResult.Restored>().Subject;

        restored.Profile.Id.Should().NotBe(originalId);
        restored.Profile.Name.Should().Be("Juniper");
        restored.Counts.Should().Be(report.Counts);
        _root.Store().List().Should().HaveCount(2);

        var unlocked = _root.Store().Unlock(restored.Profile.Id, new UnlockSecret.Passphrase(Passphrase))
            .Should().BeOfType<UnlockResult.Unlocked>().Subject;
        using var session = unlocked.Session;
        session.Data.Accounts().Should().Equal(checking);
        session.Data.Bills().Should().BeEquivalentTo(originalBills);
        session.Data.Balances(checking.Id).Single().Balance.Should().Be(Money.Usd(1_240.55m));
    }

    [Fact]
    public void On_a_new_pc_the_profile_comes_back_under_its_own_id()
    {
        var (created, _) = Seeded();
        var id = created.Session.Profile.Id;
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();
        _root.Store().Delete(id);

        var restored = Backups().Restore(BackupPath(), new UnlockSecret.RecoveryKey(created.RecoveryKey));

        restored.Should().BeOfType<RestoreResult.Restored>().Which.Profile.Id.Should().Be(id);
        restored.As<RestoreResult.Restored>().Profile.UnlocksOnThisDevice.Should().BeFalse();
    }

    [Fact]
    public void A_backup_file_reveals_nothing_without_the_key()
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();

        var text = Encoding.Latin1.GetString(File.ReadAllBytes(BackupPath()));

        text.Should().StartWith("MMBACKUP");
        text.Should().NotContain("Canary").And.NotContain("Juniper").And.NotContain("SQLite format 3").And.NotContain("PK\u0003\u0004");
        text.Should().NotContain("ThisDevice", because: "this PC's unlock must never travel inside a backup");
    }

    [Fact]
    public void Wrong_secrets_fail_and_leave_no_profile_behind()
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();

        Fails(Backups().Restore(BackupPath(), new UnlockSecret.Passphrase("moss under the old elm")), RestoreFailure.WrongSecret);
        Fails(Backups().Restore(BackupPath(), new UnlockSecret.RecoveryKey(RecoveryKey.Generate().ToString())), RestoreFailure.WrongSecret);
        Fails(Backups().Restore(BackupPath(), new UnlockSecret.RecoveryKey("nope")), RestoreFailure.MalformedRecoveryKey);
        Fails(Backups().Restore(BackupPath(), new UnlockSecret.ThisDevice()), RestoreFailure.ThisDeviceCantRestore);
        _root.Store().List().Should().ContainSingle();
        Directory.GetDirectories(_root.Locations.ProfilesDirectory, ".*").Should().BeEmpty();
    }

    [Fact]
    public void A_backup_opens_with_the_passphrase_from_when_it_was_made()
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.ChangePassphrase("a brand new passphrase here");
        created.Session.Dispose();

        Fails(Backups().Restore(BackupPath(), new UnlockSecret.Passphrase("a brand new passphrase here")), RestoreFailure.WrongSecret);
        Backups().Restore(BackupPath(), new UnlockSecret.Passphrase(Passphrase)).Should().BeOfType<RestoreResult.Restored>();
    }

    [Theory]
    [InlineData(1)] // the last byte of the final tag
    [InlineData(17)] // the whole final tag and a byte more
    [InlineData(600)]
    public void A_truncated_backup_is_refused(int bytesCut)
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();
        var bytes = File.ReadAllBytes(BackupPath());
        File.WriteAllBytes(BackupPath(), bytes[..^bytesCut]);

        var result = Backups().Restore(BackupPath(), new UnlockSecret.Passphrase(Passphrase));

        result.Should().BeOfType<RestoreResult.Failed>().Which.Reason.Should().BeOneOf(RestoreFailure.Damaged, RestoreFailure.NotABackup);
        _root.Store().List().Should().ContainSingle();
    }

    [Fact]
    public void A_changed_byte_or_trailing_data_is_refused()
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();
        var original = File.ReadAllBytes(BackupPath());

        var flipped = (byte[])original.Clone();
        flipped[^40] ^= 0x10;
        File.WriteAllBytes(BackupPath("flipped.mmbak"), flipped);
        File.WriteAllBytes(BackupPath("trailing.mmbak"), [.. original, 0x00]);

        Fails(Backups().Restore(BackupPath("flipped.mmbak"), new UnlockSecret.Passphrase(Passphrase)), RestoreFailure.Damaged);
        Fails(Backups().Restore(BackupPath("trailing.mmbak"), new UnlockSecret.Passphrase(Passphrase)), RestoreFailure.Damaged);
    }

    [Fact]
    public void Other_files_and_newer_formats_are_named_as_such()
    {
        var (created, _) = Seeded();
        Backups().Create(created.Session, BackupPath());
        created.Session.Dispose();
        File.WriteAllText(BackupPath("notes.mmbak"), "just some text");
        var newer = File.ReadAllBytes(BackupPath());
        BinaryPrimitives.WriteUInt16LittleEndian(newer.AsSpan(8), 2);
        File.WriteAllBytes(BackupPath("newer.mmbak"), newer);

        Fails(Backups().Restore(BackupPath("notes.mmbak"), new UnlockSecret.Passphrase(Passphrase)), RestoreFailure.NotABackup);
        Fails(Backups().Restore(BackupPath("missing.mmbak"), new UnlockSecret.Passphrase(Passphrase)), RestoreFailure.NotABackup);
        Fails(Backups().Restore(BackupPath("newer.mmbak"), new UnlockSecret.Passphrase(Passphrase)), RestoreFailure.UnsupportedVersion);
    }

    [Fact]
    public void Local_backups_keep_the_newest_five()
    {
        var (created, _) = Seeded();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero));
        var service = new BackupService(_root.Locations, clock);
        for (var i = 0; i < 7; i++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            service.CreateLocal(created.Session);
        }

        var folder = Path.Combine(_root.Locations.ProfileDirectory(created.Session.Profile.Id), "backups");
        Directory.GetFiles(folder, "*.mmbak").Select(Path.GetFileName).Should().HaveCount(5)
            .And.Contain("20261009T150700Z.mmbak").And.NotContain("20261009T150100Z.mmbak");
        created.Session.Dispose();
    }

    [Fact]
    public void Chunks_cant_be_dropped_reordered_or_moved()
    {
        using var key = DataKey.Generate();
        var envelopes = new[] { KeyEnvelopes.WrapWithPassphrase(key, Passphrase, KeyEnvelopeTests.Fast) };
        var payload = RandomNumberGenerator.GetBytes(5_000);
        using var written = new MemoryStream();
        BackupContainer.Write(written, envelopes, key, new MemoryStream(payload), chunkSize: 1024);
        var bytes = written.ToArray();

        RoundTrip(bytes, key).Should().Equal(payload);

        var (start, chunks) = Chunks(bytes);
        chunks.Should().HaveCount(5);
        var dropped = bytes[..start].Concat(chunks[0]).Concat(chunks[1]).Concat(chunks[2]).Concat(chunks[4]).ToArray();
        var swapped = bytes[..start].Concat(chunks[1]).Concat(chunks[0]).Concat(chunks[2]).Concat(chunks[3]).Concat(chunks[4]).ToArray();
        var noFinal = bytes[..start].Concat(chunks[0]).Concat(chunks[1]).Concat(chunks[2]).Concat(chunks[3]).ToArray();

        foreach (var tampered in new[] { dropped, swapped, noFinal })
        {
            var act = () => RoundTrip(tampered, key);
            act.Should().Throw<BackupFormatException>();
        }

        using var other = new MemoryStream();
        BackupContainer.Write(other, envelopes, key, new MemoryStream(payload), chunkSize: 1024);
        var (_, otherChunks) = Chunks(other.ToArray());
        var transplanted = bytes[..start].Concat(otherChunks[0]).Concat(chunks[1]).Concat(chunks[2]).Concat(chunks[3]).Concat(chunks[4]).ToArray();
        var transplant = () => RoundTrip(transplanted, key);
        transplant.Should().Throw<BackupFormatException>();
    }

    private static byte[] RoundTrip(byte[] container, DataKey key)
    {
        using var input = new MemoryStream(container);
        var header = BackupContainer.ReadHeader(input, out var headerBytes);
        using var output = new MemoryStream();
        BackupContainer.ReadPayload(input, header, headerBytes, key, output);
        return output.ToArray();
    }

    private static (int Start, List<byte[]> Chunks) Chunks(byte[] container)
    {
        var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(10));
        var position = 14 + headerLength;
        var start = position;
        var chunks = new List<byte[]>();
        while (position < container.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(position));
            var size = 5 + length + 16;
            chunks.Add(container[position..(position + size)]);
            position += size;
        }

        return (start, chunks);
    }

    private static void Fails(RestoreResult result, RestoreFailure expected) =>
        result.Should().BeOfType<RestoreResult.Failed>().Which.Reason.Should().Be(expected);
}
