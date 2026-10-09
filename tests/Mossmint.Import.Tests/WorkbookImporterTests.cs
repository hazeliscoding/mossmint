using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Import.Excel;
using Mossmint.Import.Excel.Xlsx;
using Mossmint.Samples.Workbooks;

namespace Mossmint.Import.Tests;

// The synthetic workbooks stand in for real ones: same shapes, invented figures.
public sealed class WorkbookImporterTests
{
    private static readonly DateOnly Today = SyntheticWorkbooks.Today;

    [Fact]
    public void Each_shape_is_recognized_by_its_sheets_not_its_file_name()
    {
        var shapes = SyntheticWorkbooks.All().Select(w => WorkbookShapes.Recognize(XlsxReader.Read(w.Content))).ToList();

        shapes.Should().Equal(WorkbookShape.Budget, WorkbookShape.CreditCard, WorkbookShape.CreditCard, WorkbookShape.SimpleInterestLoan, WorkbookShape.AutoLoan);
        var renamed = Map(new SourceFile("download (3).xlsx", SyntheticWorkbooks.PineAuto(WorkbookQuirks.None).Content));
        renamed.Files.Single().Shape.Should().Be("auto loan workbook");
    }

    [Fact]
    public void Debt_workbooks_become_accounts_terms_and_balances()
    {
        var batch = Map();

        batch.Accounts.Select(a => (a.Name, a.Kind)).Should().BeEquivalentTo(new[]
        {
            ("Fern Card", AccountKind.CreditCard), ("Sorrel Card", AccountKind.CreditCard), ("Bramble Loan", AccountKind.InstallmentLoan),
            ("Pine Auto", AccountKind.InstallmentLoan), ("Checking", AccountKind.Checking),
        });

        var bramble = batch.Debts.Single(d => d.AccountKey == "account:bramble-loan");
        var terms = bramble.Terms.Should().BeOfType<SimpleInterestLoanTerms>().Subject;
        terms.RegularPayment.Should().Be(Money.Usd(280.00m));
        terms.Rates.SegmentOn(Today)!.Rate.Should().Be(new AnnualRate(0.1650m));
        terms.PaymentDates.Rule.Should().Be(new MonthlyRule(DayOfMonth.Of(12)));
        bramble.OpenedOn.Should().Be(new DateOnly(2024, 6, 5));
        bramble.OriginalPrincipal.Should().Be(Money.Usd(9000.00m));

        var pine = batch.Debts.Single(d => d.AccountKey == "account:pine-auto").Terms.Should().BeOfType<SimpleInterestLoanTerms>().Subject;
        pine.PaymentDates.Rule.Should().Be(new MonthlyRule(DayOfMonth.Last));

        var fern = batch.Debts.Single(d => d.AccountKey == "account:fern-card").Terms.Should().BeOfType<CreditCardTerms>().Subject;
        fern.StatementClosingDay.Should().Be(DayOfMonth.Of(10));

        batch.Balances.Where(b => b.AccountKey == "account:bramble-loan").Should().ContainSingle(b => b.AsOf == new DateOnly(2026, 8, 5))
            .Which.AccruedInterest.Should().Be(Money.Usd(31.20m));
        batch.Balances.Count(b => b.AccountKey == "account:pine-auto").Should().Be(2);
    }

    [Fact]
    public void A_recorded_balance_that_differs_from_the_projection_is_queued_with_the_exact_gap()
    {
        var findings = Map().Findings.Where(f => f.Code == "balance-mismatch").ToList();

        findings.Select(f => f.SubjectKey).Should().BeEquivalentTo(["account:sorrel-card", "account:pine-auto"]);
        findings.Should().OnlyContain(f => f.Severity == FindingSeverity.NeedsReview);
        findings.Single(f => f.SubjectKey == "account:sorrel-card").Detail.Should().Contain("($37.15 higher)");
        findings.Single(f => f.SubjectKey == "account:pine-auto").Detail.Should().Contain("($455.00 higher)");
    }

    [Fact]
    public void A_payment_planned_after_its_due_day_is_a_review_item_not_a_correction()
    {
        var batch = Map();

        batch.Findings.Should().ContainSingle(f => f.Code == "due-day-mismatch").Which.SubjectKey.Should().Be("account:bramble-loan");
        var terms = (SimpleInterestLoanTerms)batch.Debts.Single(d => d.AccountKey == "account:bramble-loan").Terms;
        terms.PaymentDates.Rule.Should().Be(new MonthlyRule(DayOfMonth.Of(12)), "the workbook's plan is kept until the user changes it");
    }

    [Fact]
    public void Card_terms_the_workbook_lacks_are_flagged_as_assumptions()
    {
        var batch = Map();

        batch.Findings.Where(f => f.Code == "card-terms-assumed").Select(f => f.SubjectKey).Should().BeEquivalentTo(["account:fern-card", "account:sorrel-card"]);
    }

    [Fact]
    public void Upcoming_paychecks_become_two_income_schedules()
    {
        var income = Map().Income;

        income.Select(i => (i.Name, i.NetPay)).Should().BeEquivalentTo(new[]
        {
            ("Paycheck (15th)", Money.Usd(1600.00m)), ("Paycheck (end of month)", Money.Usd(1580.00m)),
        });
        var monthEnd = income.Single(i => i.Key == "income:end");
        monthEnd.PayDates.Rule.Should().Be(new MonthlyRule(DayOfMonth.Last));
        monthEnd.PayDates.Adjustment.Should().Be(BusinessDayAdjustment.PreviousBusinessDay);
        monthEnd.AmountOverrides.Should().ContainSingle().Which.Should().Be(new PayAmountOverride(new DateOnly(2026, 12, 31), Money.Usd(1700.00m)));
        monthEnd.DepositAccountKey.Should().Be("account:budget-spending");
    }

    [Fact]
    public void Upcoming_lines_become_bills_with_their_rhythm()
    {
        var bills = Map().Bills.ToDictionary(b => b.Key);

        bills["bill:mid:rent"].Amount.Should().Be(AmountPolicy.Fixed(Money.Usd(950.00m)));
        bills["bill:mid:rent"].DueDates.EndsOn.Should().BeNull("rent runs to the end of the sheet");
        bills["bill:mid:rent"].Purpose.Should().Be(BillPurpose.Expense);

        bills["bill:end:fern-card"].Purpose.Should().Be(BillPurpose.DebtPayment);
        bills["bill:end:fern-card"].TargetAccountKey.Should().Be("account:fern-card");
        bills["bill:end:fern-card"].DueDates.EndsOn.Should().Be(new DateOnly(2027, 1, 31));

        bills["bill:end:groceries"].Amount.Should().Be(AmountPolicy.Estimated(Money.Usd(220.00m), Money.Usd(215.00m), Money.Usd(230.00m)));

        var insurance = bills["bill:end:insurance"].DueDates;
        insurance.Overrides.Should().Equal(new SkipOccurrence(new DateOnly(2026, 11, 30)), new SkipOccurrence(new DateOnly(2027, 1, 31)));
        insurance.EndsOn.Should().Be(new DateOnly(2027, 2, 28), "the sheet's last insurance line is in February");

        var dentist = bills["bill:end:dentist"].DueDates;
        dentist.StartsOn.Should().Be(new DateOnly(2026, 11, 30));
        dentist.EndsOn.Should().Be(new DateOnly(2026, 11, 30));
    }

    [Fact]
    public void Past_blocks_are_history_and_the_newest_left_over_is_the_checking_figure()
    {
        var batch = Map();

        batch.Bills.Should().NotContain(b => b.Name == "Takeout", "lines from past checks are history, not bills");
        batch.RawRecords.Should().Contain(r => r.Kind == "budget-line" && r.Fields["label"] == "Takeout" && r.Fields["paid"] == "True");
        batch.RawRecords.Should().Contain(r => r.Kind == "budget-line" && r.Fields["label"] == "Refund" && r.Fields["kind"] == "added");

        var cash = batch.Balances.Single(b => b.AccountKey == "account:budget-spending");
        cash.AsOf.Should().Be(new DateOnly(2026, 9, 30));
        batch.Findings.Should().Contain(f => f.Code == "budget-cash" && f.Severity == FindingSeverity.NeedsReview);
        batch.Findings.Should().Contain(f => f.Code == "typed-adjustment");
    }

    [Fact]
    public void Savings_target_and_reserve_come_from_their_own_sheets()
    {
        var settings = Map().Settings!;

        settings.SavingsTarget.Should().Be(new SavingsContributionTarget(Money.Usd(100m)));
        settings.ReserveFloor.Should().Be(new CashReserveFloor(Money.Usd(150m)));
    }

    [Fact]
    public void Notes_hidden_copies_and_google_only_cells_are_reported()
    {
        var batch = Map();

        batch.Findings.Should().ContainSingle(f => f.Code == "budget-note").Which.Detail.Should().StartWith("Insurance renews in December");
        batch.Findings.Count(f => f.Code == "hidden-sheet").Should().Be(2);
        batch.Findings.Should().Contain(f => f.Code == "dashboard-not-imported");
    }

    [Fact]
    public void A_clean_set_of_workbooks_has_nothing_blocking()
    {
        Map().Findings.Should().NotContain(f => f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void Stale_cached_values_are_caught_where_they_can_be_recomputed()
    {
        var batch = Map(SyntheticWorkbooks.All(WorkbookQuirks.None with { StaleTotal = true, StaleDailyRate = true }));

        var stale = batch.Findings.Where(f => f.Code == "stale-cache").ToList();
        stale.Should().Contain(f => f.Title.StartsWith("Total Paid on the Sep 15, 2026 check", StringComparison.Ordinal));
        stale.Count(f => f.Title.Contains("saved values look out of date", StringComparison.Ordinal)).Should().Be(4);
    }

    [Fact]
    public void Dates_stored_as_plain_serial_numbers_still_read_as_dates()
    {
        var plain = Map(SyntheticWorkbooks.All(WorkbookQuirks.None with { PlainSerialDates = true }));
        var formatted = Map();

        plain.Balances.Select(b => (b.AccountKey, b.AsOf)).Should().BeEquivalentTo(formatted.Balances.Select(b => (b.AccountKey, b.AsOf)));
        plain.Findings.Should().NotContain(f => f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void A_line_listed_twice_on_one_check_is_added_up_and_noted()
    {
        var batch = Map(SyntheticWorkbooks.All(WorkbookQuirks.None with { DuplicateLine = true }));

        batch.Findings.Should().ContainSingle(f => f.Code == "duplicate-line");
        batch.Bills.Single(b => b.Key == "bill:mid:coffee").Amount.Should().Be(AmountPolicy.Fixed(Money.Usd(21.50m)));
    }

    [Fact]
    public void The_same_file_twice_is_imported_once()
    {
        var fern = SyntheticWorkbooks.FernCard(WorkbookQuirks.None);
        var batch = Map(new SourceFile(fern.FileName, fern.Content), new SourceFile("Fern Card Workbook (copy).xlsx", fern.Content));

        batch.Files.Should().ContainSingle();
        batch.Findings.Should().Contain(f => f.Code == "duplicate-file");
        batch.Accounts.Should().ContainSingle();
    }

    [Fact]
    public void Two_different_files_for_one_debt_block_the_import()
    {
        var fern = SyntheticWorkbooks.FernCard(WorkbookQuirks.None);
        var stale = SyntheticWorkbooks.FernCard(WorkbookQuirks.None with { StaleDailyRate = true });
        var batch = Map(new SourceFile("Fern Card Workbook.xlsx", fern.Content), new SourceFile("Fern Card workbook.xlsx", stale.Content));

        batch.Findings.Should().Contain(f => f.Code == "duplicate-workbook" && f.Severity == FindingSeverity.Blocking);
        batch.Accounts.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_files_block_the_import_with_a_reason()
    {
        var other = new XlsxWriter();
        other.Sheet("Recipes").Set("A1", "Soup");
        var batch = Map(new SourceFile("recipes.xlsx", other.ToBytes()), new SourceFile("notes.txt", "hello"u8.ToArray()));

        batch.Findings.Where(f => f.Severity == FindingSeverity.Blocking).Select(f => f.Code).Should().BeEquivalentTo(["unknown-workbook", "unreadable"]);
    }

    [Fact]
    public void Debt_workbooks_alone_still_give_the_forecast_their_payments()
    {
        var batch = Map(SyntheticWorkbooks.All().Skip(1).Select(w => new SourceFile(w.FileName, w.Content)).ToArray());

        batch.Bills.Should().HaveCount(4).And.OnlyContain(b => b.Purpose == BillPurpose.DebtPayment);
        batch.Findings.Should().NotContain(f => f.Code == "debt-not-in-budget");
    }

    [Fact]
    public void Every_proposal_points_back_to_a_cell()
    {
        var batch = Map();

        batch.Balances.Should().OnlyContain(b => b.Source.Location.Contains('!'));
        batch.Bills.Should().OnlyContain(b => b.Sources.Count > 0 && b.Sources.All(s => s.Location.Contains('!')));
        batch.Reference(batch.Balances[0].Source).Should().MatchRegex("^[0-9a-f]{12}:");
    }

    [Fact]
    public void Mapping_is_deterministic()
    {
        var first = Map();
        var second = Map();

        second.Bills.Select(b => b.Key).Should().Equal(first.Bills.Select(b => b.Key));
        second.Findings.Select(f => f.Fingerprint).Should().Equal(first.Findings.Select(f => f.Fingerprint));
        SyntheticWorkbooks.Budget(WorkbookQuirks.None).Content.Should().Equal(SyntheticWorkbooks.Budget(WorkbookQuirks.None).Content);
    }

    private static ImportBatch Map(IEnumerable<SyntheticWorkbook>? workbooks = null) =>
        Map([.. (workbooks ?? SyntheticWorkbooks.All()).Select(w => new SourceFile(w.FileName, w.Content))]);

    private static ImportBatch Map(params SourceFile[] files) => new WorkbookImporter().Map(files, Today, Currency.Usd);
}
