using System.Text.Json;
using Mossmint.Application.Data;
using Mossmint.Application.Export;
using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Tests;

// Export writes the profile out unencrypted: one complete JSON file, plus CSV
// files a spreadsheet opens. A spreadsheet must never run a cell as a formula.
public class ProfileExportTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
    private readonly InMemoryProfileData _data = new();
    private readonly ManualEntry _entry;
    private readonly Account _checking;

    public ProfileExportTests()
    {
        _entry = new ManualEntry(_data, new FixedClock(Now));
        _checking = _entry.AddCashAccount("Checking", 2_400m, Today);
        _entry.AddPaycheck(new PaycheckEntry("Paycheck", 2_050m, PayPattern.TwiceMonthly), _checking.Id);
        _entry.AddBill(new BillEntry("Phone, \"family\" plan", 65m, 12), _checking.Id);
        _entry.AddBill(new BillEntry("=HYPERLINK(\"http://example.test\")", 9.99m, 3), _checking.Id);
        _entry.AddLoan(new LoanEntry("Car loan", 9_800m, Today, 7.4m, 285m, 28), _checking.Id);
        _entry.AddCard(new CardEntry("Visa", 1_200m, Today, 24.99m, 100m, 20, 15), _checking.Id);
        _entry.RecordEntry(_checking.Id, "Groceries", -42.1m, new DateOnly(2026, 10, 8), "Food");
        _entry.AddGoal("Rainy day", 1_000m, 250m);
        _data.SaveSettings(_data.Settings() with { DebtPlan = new DebtPlan(Money.Usd(150m), PayoffOrder.Avalanche, []) });
    }

    private string File(string name) => ProfileExport.Files(_data, "Juniper", Now).Single(f => f.Name == name).Content;

    private static string[] Lines(string csv)
    {
        csv.Should().StartWith("﻿", "Excel reads a CSV as UTF-8 only with the byte order mark");
        csv.Should().NotContain("\n\n").And.EndWith("\r\n");
        return csv[1..^2].Split("\r\n");
    }

    [Fact]
    public void Export_writes_a_complete_file_and_three_spreadsheets()
    {
        ProfileExport.Files(_data, "Juniper", Now).Select(f => f.Name)
            .Should().Equal("profile.json", "transactions.csv", "bills.csv", "debts.csv");
    }

    [Fact]
    public void The_complete_file_holds_every_record_with_readable_names()
    {
        using var json = JsonDocument.Parse(File("profile.json"));
        var root = json.RootElement;

        (root.GetProperty("format").GetString(), root.GetProperty("version").GetInt32(), root.GetProperty("profile").GetString())
            .Should().Be(("mossmint-export", 1, "Juniper"));
        root.GetProperty("exportedAt").GetDateTimeOffset().Should().Be(Now);

        var accounts = root.GetProperty("accounts").EnumerateArray().ToList();
        accounts.Select(a => a.GetProperty("account").GetProperty("name").GetString()).Should().BeEquivalentTo("Checking", "Car loan", "Visa");
        var checking = accounts.Single(a => a.GetProperty("account").GetProperty("name").GetString() == "Checking");
        checking.GetProperty("account").GetProperty("kind").GetString().Should().Be("Checking");
        checking.GetProperty("balances").GetArrayLength().Should().Be(1);
        var groceries = checking.GetProperty("transactions").EnumerateArray().Single();
        (groceries.GetProperty("description").GetString(), groceries.GetProperty("amount").GetProperty("amount").GetDecimal(), groceries.GetProperty("amount").GetProperty("currency").GetString())
            .Should().Be(("Groceries", -42.1m, "USD"));

        root.GetProperty("bills").GetArrayLength().Should().Be(4);
        root.GetProperty("bills")[0].GetProperty("dueDates").GetProperty("rule").GetProperty("$rule").GetString().Should().Be("monthly");
        root.GetProperty("debts").EnumerateArray().Select(d => d.GetProperty("terms").GetProperty("$terms").GetString())
            .Should().BeEquivalentTo("simple-interest-loan", "credit-card");
        root.GetProperty("income").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("Paycheck");
        root.GetProperty("goals").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("Rainy day");
        var plan = root.GetProperty("settings").GetProperty("debtPlan");
        (plan.GetProperty("monthlyExtra").GetProperty("amount").GetDecimal(), plan.GetProperty("order").GetString()).Should().Be((150m, "Avalanche"));
    }

    [Fact]
    public void Transactions_use_plain_numbers_and_iso_dates()
    {
        Lines(File("transactions.csv")).Should().Equal(
            "Date,Posted,Account,Description,Category,Amount,Currency,Status,Source",
            "2026-10-08,,Checking,Groceries,Food,-42.10,USD,Posted,UserConfirmed");
    }

    [Fact]
    public void Bills_are_quoted_where_needed_and_never_run_as_formulas()
    {
        Lines(File("bills.csv")).Should().Equal(
            "Name,Purpose,Amount,Low,High,Amount kind,Currency,Repeats,Starts,Ends,Paid from,Autopay",
            "\"'=HYPERLINK(\"\"http://example.test\"\")\",Expense,9.99,,,Fixed,USD,monthly on day 3,,,Checking,no",
            "Car loan,DebtPayment,285.00,,,Fixed,USD,monthly on day 28,,,Checking,no",
            "\"Phone, \"\"family\"\" plan\",Expense,65.00,,,Fixed,USD,monthly on day 12,,,Checking,no",
            "Visa,DebtPayment,100.00,,,Fixed,USD,monthly on day 15,,,Checking,no");
    }

    [Fact]
    public void Debts_show_the_latest_balance_rate_and_payment()
    {
        Lines(File("debts.csv")).Should().Equal(
            "Name,Type,Balance,Balance as of,Currency,APR %,Payment,Opened on,Original principal",
            "Car loan,Loan,9800.00,2026-10-09,USD,7.40,285.00,,",
            "Visa,Credit card,1200.00,2026-10-09,USD,24.99,100.00,,");
    }

    // OWASP's list of characters a spreadsheet treats as the start of a formula.
    // A leading apostrophe makes the cell text; the amount column is a number and keeps its minus.
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1 refund", "'+1 refund")]
    [InlineData("-refund", "'-refund")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Coffee", "Coffee")]
    public void Text_that_looks_like_a_formula_is_kept_as_text(string description, string cell)
    {
        _entry.RecordEntry(_checking.Id, description, -3m, Today);

        Lines(File("transactions.csv")).Last().Should().Be($"2026-10-09,,Checking,{cell},,-3.00,USD,Posted,UserConfirmed");
    }
}
