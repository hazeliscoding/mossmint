using System.Text;
using Mossmint.Application.Import;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Import.Excel;
using Mossmint.Import.Transactions;
using Mossmint.Samples.Connectors;

namespace Mossmint.Persistence.Tests;

// A bank file goes into an account the user picks, through the same dry run,
// review queue and commit as every other import.
public sealed class BankFileImportTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private const string File = "Date,Description,Amount\n2026-10-01,Paycheck,1200.00\n2026-10-02,Corner Cafe,-4.50\n2026-10-03,Grocer,-61.20\n";
    private static readonly CsvMapping Signed = new(0, "yyyy-MM-dd", 1, AmountStyle.SpendingNegative, Amount: 2);
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero));
    private readonly ImportService _imports;
    private readonly IProfileSession _session;
    private readonly Account _checking = new(new AccountId(Guid.CreateVersion7()), "Household Checking", AccountKind.Checking, Currency.Usd);

    public BankFileImportTests()
    {
        _imports = new ImportService(new WorkbookImporter(), _clock);
        _session = _root.Store(_clock).Create("Juniper", "moss under the old oak", unlockOnThisDevice: false).Session;
        _session.Data.AddAccount(_checking);
    }

    public void Dispose()
    {
        _session.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void A_file_lands_in_the_chosen_account_and_importing_it_again_changes_nothing()
    {
        var plan = Apply(File, _checking);

        plan.Changes.Should().Contain(c => c.Entity == "Account" && c.Kind == ChangeKind.Unchanged);
        plan.Count(ChangeKind.Add).Should().Be(3);
        _session.Data.Accounts().Should().ContainSingle();
        _session.Data.Transactions(_checking.Id).Select(t => t.Amount).Should().BeEquivalentTo([Money.Usd(1200m), Money.Usd(-4.50m), Money.Usd(-61.20m)]);
        _session.Data.Transactions(_checking.Id).Should().OnlyContain(t => t.Provenance.Source == SourceKind.Imported);

        Apply(File, _checking).Changes.Should().OnlyContain(c => c.Kind == ChangeKind.Unchanged);
        _session.Data.Transactions(_checking.Id).Should().HaveCount(3);
    }

    [Fact]
    public void A_file_can_create_the_account_it_fills_and_find_it_again_next_time()
    {
        var joint = new Account(new AccountId(Guid.CreateVersion7()), "Joint Checking", AccountKind.Checking, Currency.Usd);

        Apply(File, joint).Changes.Should().ContainSingle(c => c.Entity == "Account" && c.Kind == ChangeKind.Add);

        _session.Data.Accounts().Should().ContainSingle(a => a.Id == joint.Id && a.Name == "Joint Checking");
        Apply(File, joint).Changes.Should().OnlyContain(c => c.Kind == ChangeKind.Unchanged);
    }

    [Fact]
    public void A_voided_imported_entry_stays_void_when_the_file_comes_again()
    {
        Apply(File, _checking);
        var cafe = _session.Data.Transactions(_checking.Id).Single(t => t.Description == "Corner Cafe");
        _session.Data.VoidTransaction(cafe.Id);

        var plan = Apply(File, _checking);

        plan.Changes.Should().ContainSingle(c => c.Kind == ChangeKind.KeepEdited).Which.Name.Should().Be("Household Checking");
        _session.Data.Transactions(_checking.Id).Single(t => t.Id == cafe.Id).Status.Should().Be(TransactionStatus.Void);
    }

    [Fact]
    public void An_account_fed_by_a_connection_refuses_a_file()
    {
        var sync = FakeBank.First(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        ImportService.Commit(_session, _imports.Plan(_session,
            ConnectorBatches.From(sync, FakeBank.DisplayName, Today, Currency.Usd, new Dictionary<string, Account> { [FakeBank.Checking.ExternalId] = _checking })));

        var plan = Plan(File, _checking);

        plan.CanCommit.Should().BeFalse();
        plan.Findings.Should().ContainSingle(f => f.Code == "one-source").Which.Title.Should().Contain("a connection (fake-bank)");
    }

    [Fact]
    public void Entries_typed_on_the_same_days_are_queued_for_review()
    {
        _session.Data.RecordTransaction(new Transaction(new TransactionId(Guid.CreateVersion7()), _checking.Id, new DateOnly(2026, 10, 2), Money.Usd(-4.50m),
            TransactionStatus.Posted, "Coffee", Provenance.UserConfirmed(new DateOnly(2026, 10, 2))));
        _session.Data.RecordTransaction(new Transaction(new TransactionId(Guid.CreateVersion7()), _checking.Id, new DateOnly(2026, 9, 20), Money.Usd(-8m),
            TransactionStatus.Posted, "Older", Provenance.UserConfirmed(new DateOnly(2026, 9, 20))));

        var plan = Apply(File, _checking);

        plan.CanCommit.Should().BeTrue();
        _session.Imports.ReviewItems().Should().ContainSingle(r => r.Code == "typed-overlap").Which.Detail.Should().StartWith("1 entry you added");
    }

    [Fact]
    public void A_statement_balance_is_confirmed_and_an_overlapping_statement_adds_only_whats_new()
    {
        ApplyOfx(Statement(("T-1", "20261001", "1200.00"), ("T-2", "20261002", "-4.50")), "1295.50", "20261002");

        var plan = ApplyOfx(Statement(("T-2", "20261002", "-4.50"), ("T-3", "20261011", "-20.00")), "1275.50", "20261011");

        plan.Changes.Where(c => c.Entity == "Transaction").Select(c => c.Kind).Should().BeEquivalentTo([ChangeKind.Unchanged, ChangeKind.Add]);
        _session.Data.Transactions(_checking.Id).Should().HaveCount(3);
        var newest = _session.Data.Balances(_checking.Id)[0];
        newest.Balance.Should().Be(Money.Usd(1275.50m));
        newest.Provenance.Confidence.Should().Be(Confidence.Confirmed, "a balance the bank reported can clear needs confirmation");
        _session.Data.Transactions(_checking.Id).Should().OnlyContain(t => t.Provenance.Source == SourceKind.Imported);
    }

    [Fact]
    public void A_statement_cannot_join_an_account_fed_by_csv()
    {
        Apply(File, _checking);

        var file = new SourceFile("statement.ofx", Encoding.UTF8.GetBytes(Statement(("T-9", "20261011", "-20.00"))));
        var ofx = OfxReader.Read(file.Content);
        var plan = _imports.Plan(_session, OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = _checking }, Today, Currency.Usd));

        plan.Findings.Should().ContainSingle(f => f.Code == "one-source").Which.Title.Should().Contain("CSV files");
    }

    [Fact]
    public void The_same_transaction_twice_in_one_batch_blocks_instead_of_failing_at_save()
    {
        var key = ImportKeys.Account(_checking.Id);
        var source = new SourceRef("bank.ofx", "x");
        var batch = new ImportBatch("test", ImportOrigin.BankFile, Today, Currency.Usd, [new SourceFileInfo("bank.ofx", "00", 0, "test")])
        {
            Accounts = [new ProposedAccount(key, _checking.Name, _checking.Kind, [source])],
            Transactions =
            [
                new ProposedTransaction(key, "ofx:1", Today, Money.Usd(-1m), TransactionStatus.Posted, "One", source),
                new ProposedTransaction(key, "ofx:1", Today, Money.Usd(-2m), TransactionStatus.Posted, "Other", source),
            ],
        };

        var plan = _imports.Plan(_session, batch);

        plan.CanCommit.Should().BeFalse();
        plan.Findings.Should().ContainSingle(f => f.Code == "duplicate-key");
    }

    [Fact]
    public void A_files_mapping_is_remembered_inside_the_profile()
    {
        _session.Imports.FileMapping("3f2a").Should().BeNull();

        _session.Imports.SaveFileMapping("3f2a", "{\"first\":true}");
        _session.Imports.SaveFileMapping("3f2a", "{\"second\":true}");

        _session.Imports.FileMapping("3f2a").Should().Be("{\"second\":true}");
        _session.Imports.FileMapping("other").Should().BeNull();
    }

    private static string Statement(params (string Id, string Date, string Amount)[] rows) =>
        "<?xml version=\"1.0\"?><OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD</CURDEF>" +
        "<BANKACCTFROM><BANKID>011000015</BANKID><ACCTID>000555000111</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM><BANKTRANLIST>" +
        string.Concat(rows.Select(r => $"<STMTTRN><TRNTYPE>OTHER</TRNTYPE><DTPOSTED>{r.Date}</DTPOSTED><TRNAMT>{r.Amount}</TRNAMT><FITID>{r.Id}</FITID><NAME>Entry {r.Id}</NAME></STMTTRN>")) +
        "</BANKTRANLIST>" +
        "</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private ImportPlan ApplyOfx(string text, string balance, string asOf) => ApplyOfx(text.Replace("</BANKTRANLIST>",
        $"</BANKTRANLIST><LEDGERBAL><BALAMT>{balance}</BALAMT><DTASOF>{asOf}</DTASOF></LEDGERBAL>", StringComparison.Ordinal));

    private ImportPlan ApplyOfx(string text)
    {
        var file = new SourceFile("statement.ofx", Encoding.UTF8.GetBytes(text));
        var ofx = OfxReader.Read(file.Content);
        var plan = _imports.Plan(_session, OfxImport.Map(file, ofx, new Dictionary<string, Account> { [OfxImport.AccountKey(ofx.Statements[0])] = _checking }, Today, Currency.Usd));
        ImportService.Commit(_session, plan);
        return plan;
    }

    private ImportPlan Plan(string text, Account target)
    {
        var file = new SourceFile("bank.csv", Encoding.UTF8.GetBytes(text));
        var batch = CsvImport.Map(file, CsvReader.Read(file.Content), Signed, new Dictionary<string, Account> { [""] = target }, Today, Currency.Usd);
        return _imports.Plan(_session, batch);
    }

    private ImportPlan Apply(string text, Account target)
    {
        var plan = Plan(text, target);
        ImportService.Commit(_session, plan);
        return plan;
    }
}
