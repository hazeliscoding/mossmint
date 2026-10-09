using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Planning;
using Mossmint.Import.Excel;
using Mossmint.Persistence.Backups;
using Mossmint.Samples.Workbooks;

namespace Mossmint.Persistence.Tests;

// End to end: synthetic workbooks into a real encrypted profile.
public sealed class ImportTests : IDisposable
{
    private const string Passphrase = "moss under the old oak";
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 9, 14, 0, 0, TimeSpan.Zero));
    private readonly ImportService _imports;

    public ImportTests() => _imports = new ImportService(new WorkbookImporter(), _clock);

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_committed_import_lands_whole_with_its_review_queue()
    {
        using var session = NewProfile();
        var plan = _imports.Preview(session, Files());

        plan.CanCommit.Should().BeTrue(plan.Report());
        ImportService.Commit(session, plan);

        session.Data.Accounts().Should().HaveCount(5);
        session.Data.Debts().Should().HaveCount(4);
        session.Data.Income().Should().HaveCount(2);
        session.Data.Bills().Should().HaveCount(plan.Batch.Bills.Count);
        session.Data.Settings().ReserveFloor.Should().Be(new CashReserveFloor(Money.Usd(150m)));
        var review = session.Imports.ReviewItems();
        review.Should().HaveCount(plan.Findings.Count(f => f.Severity == FindingSeverity.NeedsReview));
        review.Should().OnlyContain(r => r.Status == ReviewStatus.Open);
        session.Imports.Runs().Should().ContainSingle().Which.Report.Should().Contain("SHA-256");
        session.Imports.RawRecords(session.Imports.Runs()[0].Id).Should().HaveCount(plan.Batch.RawRecords.Count);
    }

    [Fact]
    public void Imported_figures_are_labelled_imported_until_confirmed()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));

        var pine = session.Data.Accounts().Single(a => a.Name == "Pine Auto");
        var balances = session.Data.Balances(pine.Id);
        balances.Should().HaveCount(2);
        balances.Should().OnlyContain(b => b.Provenance.Source == SourceKind.Imported && b.Provenance.Confidence == Confidence.NeedsReview);
        balances[0].Provenance.AsOf.Should().Be(new DateOnly(2026, 9, 30), "the newest balance comes first");
        balances[0].Provenance.Reference.Should().MatchRegex("^[0-9a-f]{12}:Settings!B10$");
    }

    [Fact]
    public void Importing_the_same_files_again_changes_nothing()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));
        var before = session.Data.Counts();

        var again = _imports.Preview(session, Files());

        again.Changes.Should().OnlyContain(c => c.Kind == ChangeKind.Unchanged);
        again.Commit.ReviewItems.Should().BeEmpty("findings already queued aren't queued twice");
        ImportService.Commit(session, again);
        var after = session.Data.Counts();
        after.Should().Be(before with { AuditEvents = after.AuditEvents, ImportRuns = before.ImportRuns + 1 });
    }

    [Fact]
    public void A_dismissed_finding_stays_dismissed_on_reimport()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));
        var item = session.Imports.ReviewItems().First(r => r.Code == "due-day-mismatch");
        session.Imports.CloseReviewItem(item.Id, ReviewStatus.Dismissed, "Paying on the 12th is fine with the lender.");

        ImportService.Commit(session, _imports.Preview(session, Files()));

        session.Imports.ReviewItems().Where(r => r.Code == "due-day-mismatch").Should().ContainSingle()
            .Which.Status.Should().Be(ReviewStatus.Dismissed);
    }

    [Fact]
    public void A_bill_edited_in_Mossmint_is_never_overwritten_by_a_reimport()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));
        var rent = session.Data.Bills().Single(b => b.Name == "Rent");
        session.Data.SaveBill(rent with { Amount = AmountPolicy.Fixed(Money.Usd(975.00m)) });

        var plan = _imports.Preview(session, Files());

        plan.Changes.Should().ContainSingle(c => c.Key == "bill:mid:rent").Which.Kind.Should().Be(ChangeKind.KeepEdited);
        ImportService.Commit(session, plan);
        session.Data.Bills().Single(b => b.Name == "Rent").Amount.Should().Be(AmountPolicy.Fixed(Money.Usd(975.00m)));
    }

    [Fact]
    public void A_changed_source_updates_what_the_import_created()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));

        var plan = _imports.Preview(session, Files(WorkbookQuirks.None with { DuplicateLine = true }));

        plan.Changes.Should().Contain(c => c.Kind == ChangeKind.Add && c.Key == "bill:mid:coffee");
        plan.Changes.Where(c => c.Entity == "Bill" && c.Key != "bill:mid:coffee").Should().OnlyContain(c => c.Kind == ChangeKind.Unchanged);
    }

    [Fact]
    public void A_record_missing_from_the_source_is_kept_and_queued()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files(WorkbookQuirks.None with { DuplicateLine = true })));

        var plan = _imports.Preview(session, Files());
        ImportService.Commit(session, plan);

        plan.Changes.Should().ContainSingle(c => c.Kind == ChangeKind.NotInSource).Which.Key.Should().Be("bill:mid:coffee");
        session.Data.Bills().Should().Contain(b => b.Name == "Coffee");
        session.Imports.ReviewItems().Should().Contain(r => r.Code == "not-in-source" && r.SubjectKey == "bill:mid:coffee");
    }

    [Fact]
    public void A_failed_import_leaves_the_profile_untouched()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));
        var before = session.Data.Counts();
        var plan = _imports.Preview(session, Files(WorkbookQuirks.None with { DuplicateLine = true }));

        // A balance for an account that doesn't exist breaks a foreign key halfway through the write.
        var orphan = new BalanceSnapshot(new AccountId(Guid.NewGuid()), Money.Usd(1m), Provenance.Imported(new DateOnly(2026, 10, 1)));
        var broken = plan.Commit with { Balances = [.. plan.Commit.Balances, orphan] };
        var act = () => session.Imports.Commit(broken);

        act.Should().Throw<Exception>();
        session.Data.Counts().Should().Be(before);
        session.Imports.Runs().Should().ContainSingle();
        session.Data.Bills().Should().NotContain(b => b.Name == "Coffee");
    }

    [Fact]
    public void A_preview_that_another_import_overtook_is_refused()
    {
        using var session = NewProfile();
        var first = _imports.Preview(session, Files());
        var second = _imports.Preview(session, Files());
        ImportService.Commit(session, first);

        var act = () => ImportService.Commit(session, second);

        act.Should().Throw<StaleImportPlanException>();
    }

    [Fact]
    public void Blocking_problems_prevent_the_commit()
    {
        using var session = NewProfile();
        var plan = _imports.Preview(session, [new SourceFile("notes.txt", "not a workbook"u8.ToArray())]);

        plan.CanCommit.Should().BeFalse();
        var act = () => ImportService.Commit(session, plan);
        act.Should().Throw<InvalidOperationException>();
        session.Imports.Runs().Should().BeEmpty();
    }

    [Fact]
    public void Each_import_takes_an_encrypted_snapshot_first()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));

        var folder = Path.Combine(_root.Locations.ProfileDirectory(session.Profile.Id), "snapshots");
        var snapshot = Directory.GetFiles(folder, "*-pre-import.db").Should().ContainSingle().Subject;
        File.ReadAllBytes(snapshot).Take(15).Should().NotEqual("SQLite format 3"u8.ToArray());
    }

    [Fact]
    public void A_backup_carries_the_review_queue_and_import_history()
    {
        using var session = NewProfile();
        ImportService.Commit(session, _imports.Preview(session, Files()));
        var backups = new BackupService(_root.Locations, _clock);
        var path = Path.Combine(_root.Path, "juniper.mmbak");

        var report = backups.Create(session, path);
        var restored = backups.Restore(path, new UnlockSecret.Passphrase(Passphrase));

        report.Counts.ReviewItems.Should().BeGreaterThan(0);
        report.Counts.ImportRuns.Should().Be(1);
        restored.Should().BeOfType<Application.Backups.RestoreResult.Restored>().Which.Counts.Should().Be(report.Counts);
    }

    [Fact]
    public void The_source_files_are_never_written()
    {
        var folder = Path.Combine(_root.Path, "workbooks");
        Directory.CreateDirectory(folder);
        foreach (var workbook in SyntheticWorkbooks.All())
        {
            File.WriteAllBytes(Path.Combine(folder, workbook.FileName), workbook.Content);
        }

        var paths = Directory.GetFiles(folder);
        var stamps = paths.ToDictionary(p => p, File.GetLastWriteTimeUtc);
        using var session = NewProfile();
        var files = paths.Select(SourceFiles.Read).ToList();
        ImportService.Commit(session, _imports.Preview(session, files));

        foreach (var (path, file) in paths.Zip(files))
        {
            SourceFiles.IsUnchanged(path, file.Sha256).Should().BeTrue();
            File.GetLastWriteTimeUtc(path).Should().Be(stamps[path]);
        }
    }

    private IProfileSession NewProfile() => _root.Store(_clock).Create("Juniper", Passphrase, unlockOnThisDevice: false).Session;

    private static List<SourceFile> Files(WorkbookQuirks? quirks = null) =>
        [.. SyntheticWorkbooks.All(quirks).Select(w => new SourceFile(w.FileName, w.Content))];
}
