using System.Text;
using Mossmint.Import.Transactions;

namespace Mossmint.Import.Tests;

public sealed class CsvGuessTests
{
    private static CsvGuess Guess(string text) => CsvMappings.Guess(CsvReader.Read(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Guesses_a_plain_bank_file()
    {
        var guess = Guess("Transaction Date,Description,Amount,Balance\n2026-10-01,Paycheck,1200.00,1300.00\n2026-10-02,Corner Cafe,-4.50,1295.50\n");

        guess.Preset.Should().BeNull();
        guess.Mapping.Should().Be(new CsvMapping(0, "yyyy-MM-dd", 1, AmountStyle.SpendingNegative, Amount: 2));
        guess.DateFormats.Should().Equal("yyyy-MM-dd");
    }

    [Fact]
    public void Guesses_separate_debit_and_credit_columns_and_decimal_commas()
    {
        var guess = Guess("Datum;Omschrijving;Debit;Credit\n02.10.2026;Bakker;4,50;\n03.10.2026;Salaris;;1.200,00\n");

        guess.Mapping.Amounts.Should().Be(AmountStyle.SeparateColumns);
        guess.Mapping.MoneyOut.Should().Be(2);
        guess.Mapping.MoneyIn.Should().Be(3);
        guess.Mapping.DecimalSeparator.Should().Be(',');
        guess.Mapping.Date.Should().Be(0);
        guess.DateFormats.Should().Contain("d.M.yyyy");
    }

    [Fact]
    public void Says_when_a_date_could_be_month_first_or_day_first()
    {
        var guess = Guess("Date,Description,Amount\n01/02/2026,Cafe,-4.50\n03/04/2026,Shop,-1.00\n");

        guess.DateFormats.Should().Equal("M/d/yyyy", "d/M/yyyy");
    }

    [Fact]
    public void Settles_month_first_when_a_day_is_past_twelve()
    {
        var guess = Guess("Date,Description,Amount\n01/02/2026,Cafe,-4.50\n01/25/2026,Shop,-1.00\n");

        guess.DateFormats.Should().Equal("M/d/yyyy");
        guess.Mapping.DateFormat.Should().Be("M/d/yyyy");
    }

    [Fact]
    public void Finds_the_columns_of_a_file_without_a_header()
    {
        var guess = Guess("\"01/02/2026\",\"-4.50\",\"*\",\"\",\"CORNER CAFE\"\n\"01/15/2026\",\"1200.00\",\"*\",\"\",\"PAYROLL\"\n");

        guess.Mapping.Should().Be(new CsvMapping(0, "M/d/yyyy", 4, AmountStyle.SpendingNegative, Amount: 1));
    }

    [Theory]
    [InlineData("Account,Flag,Date,Payee,Category Group/Category,Category Group,Category,Memo,Outflow,Inflow,Cleared\nChecking,,10/02/2026,Cafe,Food: Coffee,Food,Coffee,,$4.50,$0.00,Cleared\n", "YNAB")]
    [InlineData("Date,Merchant,Category,Account,Original Statement,Notes,Amount,Tags\n2026-10-02,Cafe,Coffee,Checking,CAFE 123,,-4.50,\n", "Monarch")]
    [InlineData("Account,Date,Payee,Notes,Category,Amount,Split_Amount,Cleared\nChecking,2026-10-02,Cafe,,Coffee,-4.50,,true\n", "Actual")]
    [InlineData("\"Date\",\"Description\",\"Original Description\",\"Amount\",\"Transaction Type\",\"Category\",\"Account Name\",\"Labels\",\"Notes\"\n\"10/02/2026\",\"Cafe\",\"CAFE 123\",\"4.50\",\"debit\",\"Coffee\",\"Checking\",\"\",\"\"\n", "Mint")]
    [InlineData("Date,Posted,Account,Description,Category,Amount,Currency,Status,Source\r\n2026-10-02,,Checking,Cafe,,-4.50,USD,Posted,UserConfirmed\r\n", "Mossmint")]
    public void Recognizes_exports_from_other_apps(string text, string preset)
    {
        var guess = Guess(text);

        guess.Preset.Should().Be(preset);
        guess.Mapping.Account.Should().NotBeNull();
        var table = CsvReader.Read(Encoding.UTF8.GetBytes(text));
        table.Header[guess.Mapping.Description].Should().BeOneOf("Payee", "Merchant", "Description");
    }

    [Fact]
    public void Each_preset_reads_its_sample_row_as_a_four_fifty_coffee()
    {
        string[] files =
        [
            "Account,Flag,Date,Payee,Category Group/Category,Category Group,Category,Memo,Outflow,Inflow,Cleared\nChecking,,10/02/2026,Cafe,Food: Coffee,Food,Coffee,,$4.50,$0.00,Cleared\n",
            "Date,Merchant,Category,Account,Original Statement,Notes,Amount,Tags\n2026-10-02,Cafe,Coffee,Checking,CAFE 123,,-4.50,\n",
            "Account,Date,Payee,Notes,Category,Amount,Split_Amount,Cleared\nChecking,2026-10-02,Cafe,,Coffee,-4.50,,true\n",
            "\"Date\",\"Description\",\"Original Description\",\"Amount\",\"Transaction Type\",\"Category\",\"Account Name\",\"Labels\",\"Notes\"\n\"10/02/2026\",\"Cafe\",\"CAFE 123\",\"4.50\",\"debit\",\"Coffee\",\"Checking\",\"\",\"\"\n",
            "Date,Posted,Account,Description,Category,Amount,Currency,Status,Source\r\n2026-10-02,,Checking,Cafe,,-4.50,USD,Posted,UserConfirmed\r\n",
        ];
        var checking = new Domain.Accounts.Account(new Domain.Accounts.AccountId(Guid.NewGuid()), "Checking", Domain.Accounts.AccountKind.Checking, Domain.Currency.Usd);

        foreach (var text in files)
        {
            var file = new Application.Import.SourceFile("export.csv", Encoding.UTF8.GetBytes(text));
            var table = CsvReader.Read(file.Content);
            var batch = CsvImport.Map(file, table, CsvMappings.Guess(table).Mapping, new Dictionary<string, Domain.Accounts.Account> { ["Checking"] = checking }, new DateOnly(2026, 10, 12), Domain.Currency.Usd);

            batch.Findings.Should().BeEmpty(text);
            batch.Transactions.Should().ContainSingle(text).Which.Should().BeEquivalentTo(new { Date = new DateOnly(2026, 10, 2), Amount = Domain.Money.Usd(-4.50m), Description = "Cafe" });
        }
    }

    [Fact]
    public void The_fingerprint_follows_the_header_not_the_rows()
    {
        var a = CsvMappings.Fingerprint(CsvReader.Read(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-10-01,A,1\n")));
        var b = CsvMappings.Fingerprint(CsvReader.Read(Encoding.UTF8.GetBytes("date, description ,AMOUNT\n2026-11-01,B,2\n2026-11-02,C,3\n")));
        var c = CsvMappings.Fingerprint(CsvReader.Read(Encoding.UTF8.GetBytes("Date,Description,Amount,Balance\n2026-10-01,A,1,5\n")));

        a.Should().Be(b);
        a.Should().NotBe(c);
    }
}
