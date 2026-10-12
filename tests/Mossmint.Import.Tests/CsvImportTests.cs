using System.Text;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

public sealed class CsvImportTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly Account Checking = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-00000000000a")), "Everyday Checking", AccountKind.Checking, Currency.Usd);
    private static readonly Account Card = new(new AccountId(Guid.Parse("00000000-0000-0000-0000-00000000000b")), "Rewards Card", AccountKind.CreditCard, Currency.Usd);
    private static readonly CsvMapping Signed = new(Date: 0, DateFormat: "yyyy-MM-dd", Description: 1, AmountStyle.SpendingNegative, Amount: 2);

    [Fact]
    public void Spending_shown_as_negative_lands_as_is_on_checking_and_flipped_on_a_card()
    {
        const string File = "Date,Description,Amount\n2026-10-01,Paycheck,1200.00\n2026-10-02,Corner Cafe,-4.50\n";

        var checking = Map(File, Signed, Checking);
        var card = Map(File, Signed, Card);

        checking.Transactions.Select(t => t.Amount).Should().Equal(Money.Usd(1200m), Money.Usd(-4.50m));
        card.Transactions.Select(t => t.Amount).Should().Equal(new[] { Money.Usd(-1200m), Money.Usd(4.50m) }, "on a card a charge raises what's owed and a payment lowers it");
        checking.Transactions.Should().OnlyContain(t => t.AccountKey == ImportKeys.Account(Checking.Id) && t.Status == TransactionStatus.Posted);
        checking.Transactions[1].Date.Should().Be(new DateOnly(2026, 10, 2));
        checking.Transactions[1].Description.Should().Be("Corner Cafe");
        checking.Transactions[1].Source.Should().Be(new SourceRef("bank.csv", "line 3"));
        checking.Accounts.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Key = ImportKeys.Account(Checking.Id), Checking.Name, Checking.Kind });
        checking.Origin.Should().Be(ImportOrigin.BankFile);
        checking.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Spending_shown_as_positive_is_turned_around()
    {
        var batch = Map("Date,Description,Amount\n2026-10-02,Corner Cafe,4.50\n2026-10-03,Refund,-2.00\n", Signed with { Amounts = AmountStyle.SpendingPositive }, Checking);

        batch.Transactions.Select(t => t.Amount).Should().Equal(Money.Usd(-4.50m), Money.Usd(2m));
    }

    [Fact]
    public void Separate_money_out_and_money_in_columns_are_combined()
    {
        var mapping = new CsvMapping(0, "M/d/yyyy", 1, AmountStyle.SeparateColumns, MoneyOut: 2, MoneyIn: 3);

        var batch = Map("Date,Payee,Outflow,Inflow\n10/1/2026,Rent,\"$1,234.50\",$0.00\n10/2/2026,Refund,,$20.00\n", mapping, Checking);

        batch.Transactions.Select(t => t.Amount).Should().Equal(Money.Usd(-1234.50m), Money.Usd(20m));
    }

    [Fact]
    public void Positive_amounts_take_their_direction_from_a_debit_or_credit_column()
    {
        var mapping = new CsvMapping(0, "M/d/yyyy", 1, AmountStyle.WithDirection, Amount: 2, Direction: 3);

        var batch = Map("Date,Description,Amount,Transaction Type\n10/01/2026,Paycheck,1200.00,credit\n10/02/2026,Corner Cafe,4.50,debit\n", mapping, Checking);

        batch.Transactions.Select(t => t.Amount).Should().Equal(Money.Usd(1200m), Money.Usd(-4.50m));
    }

    [Theory]
    [InlineData("-4.50", '.', "-4.50")]
    [InlineData("(4.50)", '.', "-4.50")]
    [InlineData("4.50-", '.', "-4.50")]
    [InlineData("$1,234.56", '.', "1234.56")]
    [InlineData("-$1,234.56", '.', "-1234.56")]
    [InlineData("+12", '.', "12")]
    [InlineData("1.234,56", ',', "1234.56")]
    [InlineData("€ 3,50", ',', "3.50")]
    [InlineData("1 234,56", ',', "1234.56")]
    public void Reads_amounts_the_way_banks_write_them(string text, char decimalSeparator, string expected)
    {
        CsvValues.TryAmount(text, decimalSeparator, out var amount).Should().BeTrue();
        amount.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("12..5")]
    [InlineData("=1+2")]
    public void Refuses_text_that_isnt_an_amount(string text) =>
        CsvValues.TryAmount(text, '.', out _).Should().BeFalse();

    [Theory]
    [InlineData("(-4.50)")]
    [InlineData("-4.50-")]
    [InlineData("123456789012345678901234567890")]
    public void Refuses_amounts_with_two_signs_or_too_many_digits(string text) =>
        CsvValues.TryAmount(text, '.', out _).Should().BeFalse();

    [Theory]
    [InlineData("Date,Payee,Outflow,Inflow\n10/1/2026,Cafe,-4.50,\n", AmountStyle.SeparateColumns)]
    [InlineData("Date,Payee,Amount,Type\n10/1/2026,Cafe,-4.50,debit\n", AmountStyle.WithDirection)]
    public void A_negative_amount_where_a_column_already_gives_the_direction_blocks(string text, AmountStyle style)
    {
        var mapping = new CsvMapping(0, "M/d/yyyy", 1, style, Amount: 2, MoneyOut: 2, MoneyIn: 3, Direction: 3);

        var batch = Map(text, mapping, Checking);

        batch.Transactions.Should().BeEmpty();
        batch.Findings.Should().ContainSingle(f => f.Code == "csv-amount" && f.Severity == FindingSeverity.Blocking);
    }

    [Theory]
    [InlineData("01/02/2026", "M/d/yyyy", 2026, 1, 2)]
    [InlineData("1/2/26", "M/d/yy", 2026, 1, 2)]
    [InlineData("02/01/2026", "d/M/yyyy", 2026, 1, 2)]
    [InlineData("02.01.2026", "d.M.yyyy", 2026, 1, 2)]
    [InlineData("2026-01-02 13:45:00", "yyyy-MM-dd", 2026, 1, 2)]
    [InlineData("2026-01-02T00:00:00Z", "yyyy-MM-dd", 2026, 1, 2)]
    [InlineData("Jan 2, 2026", "MMM d, yyyy", 2026, 1, 2)]
    public void Reads_dates_in_the_chosen_format(string text, string format, int year, int month, int day)
    {
        CsvValues.TryDate(text, format, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Fact]
    public void Identical_rows_get_distinct_ids_that_an_overlapping_file_repeats()
    {
        var first = Map("Date,Description,Amount\n2026-10-02,Coffee,-3.00\n2026-10-02,Coffee,-3.00\n2026-10-02,Bagel,-2.00\n", Signed, Checking);
        var overlapping = Map("Date,Description,Amount\n2026-10-02,Coffee,-3.00\n2026-10-02,Bagel,-2.00\n2026-10-02,Coffee,-3.00\n2026-10-03,Lunch,-9.00\n", Signed, Checking);

        first.Transactions.Select(t => t.ExternalId).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("csv:", StringComparison.Ordinal));
        overlapping.Transactions.Take(3).Select(t => t.ExternalId).Should().BeEquivalentTo(first.Transactions.Select(t => t.ExternalId));
    }

    [Fact]
    public void A_row_without_a_date_is_left_out_with_a_note()
    {
        var batch = Map("Date,Description,Amount\n2026-10-01,Paycheck,1200.00\n2026-10-02,Corner Cafe,-4.50\nTotal,,1195.50\n", Signed, Checking);

        batch.Transactions.Should().HaveCount(2);
        batch.Findings.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Code = "csv-row-skipped", Severity = FindingSeverity.Info });
        batch.Findings[0].Sources.Should().Equal(new SourceRef("bank.csv", "line 4"));
    }

    [Fact]
    public void Dates_that_mostly_fail_mean_the_wrong_format_and_block()
    {
        var batch = Map("Date,Description,Amount\n10/01/2026,Paycheck,1200.00\n10/02/2026,Corner Cafe,-4.50\n", Signed, Checking);

        batch.Findings.Should().Contain(f => f.Code == "csv-date-format" && f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void A_dated_row_with_an_unreadable_amount_blocks_and_names_its_line()
    {
        var batch = Map("Date,Description,Amount\n2026-10-01,Paycheck,1200.00\n2026-10-02,Corner Cafe,four fifty\n", Signed, Checking);

        var finding = batch.Findings.Should().ContainSingle(f => f.Severity == FindingSeverity.Blocking).Which;
        finding.Code.Should().Be("csv-amount");
        finding.Sources.Should().Equal(new SourceRef("bank.csv", "line 3"));
    }

    [Fact]
    public void Fractions_of_a_cent_block()
    {
        var batch = Map("Date,Description,Amount\n2026-10-02,Fuel,-30.129\n", Signed, Checking);

        batch.Findings.Should().ContainSingle(f => f.Code == "csv-fractional-amount" && f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void Rows_for_an_account_with_no_destination_are_left_out_with_a_note()
    {
        var mapping = Signed with { Description = 2, Amount = 3, Account = 1 };
        const string File = "Date,Account,Description,Amount\n2026-10-01,Checking,Paycheck,1200.00\n2026-10-02,Old Card,Corner Cafe,4.50\n2026-10-03,Old Card,Shop,1.00\n";

        var batch = CsvImport.Map(Source(File), CsvReader.Read(Source(File).Content), mapping, new Dictionary<string, Account> { ["Checking"] = Checking }, Today, Currency.Usd);

        batch.Transactions.Should().ContainSingle().Which.Description.Should().Be("Paycheck");
        batch.Findings.Should().ContainSingle(f => f.Code == "csv-account-skipped").Which.Detail.Should().Contain("2 rows").And.Contain("Old Card");
    }

    [Fact]
    public void Mossmint_exports_read_back_with_their_formula_escaping_undone()
    {
        var mapping = new CsvMapping(0, "yyyy-MM-dd", 3, AmountStyle.AsMossmint, Amount: 5, Account: 2, Status: 7, UnescapeFormulas: true);
        const string File = "Date,Posted,Account,Description,Category,Amount,Currency,Status,Source\r\n" +
            "2026-10-01,,Everyday Checking,'=SUM(A1),,-4.50,USD,Posted,UserConfirmed\r\n" +
            "2026-10-02,,Rewards Card,'+1 fee,,25.00,USD,Pending,UserConfirmed\r\n" +
            "2026-10-03,,Rewards Card,Mistake,,9.99,USD,Void,UserConfirmed\r\n";
        var targets = new Dictionary<string, Account> { ["Everyday Checking"] = Checking, ["Rewards Card"] = Card };

        var batch = CsvImport.Map(Source(File), CsvReader.Read(Source(File).Content), mapping, targets, Today, Currency.Usd);

        batch.Transactions.Select(t => (t.Description, t.Amount, t.Status)).Should().Equal(
            ("=SUM(A1)", Money.Usd(-4.50m), TransactionStatus.Posted),
            ("+1 fee", Money.Usd(25m), TransactionStatus.Pending));
        batch.Findings.Should().ContainSingle(f => f.Code == "csv-void-skipped");
    }

    private static SourceFile Source(string text) => new("bank.csv", Encoding.UTF8.GetBytes(text));

    private static ImportBatch Map(string text, CsvMapping mapping, Account target)
    {
        var file = Source(text);
        return CsvImport.Map(file, CsvReader.Read(file.Content), mapping, new Dictionary<string, Account> { [""] = target }, Today, Currency.Usd);
    }
}
