using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Application.Export;

public sealed record ExportFile(string Name, string Content);

// The profile written out unencrypted; the caller warns first. profile.json holds
// every record as Mossmint keeps it, to keep or to move to another app. The CSV
// files are for a spreadsheet (RFC 4180): numbers and ISO dates are culture-free,
// so Excel reads them the same in any locale, and text can't run as a formula.
public static class ProfileExport
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<ExportFile> Files(IProfileData data, string profileName, DateTimeOffset exportedAt)
    {
        var accounts = data.Accounts();
        var names = accounts.ToDictionary(a => a.Id, a => a.Name);
        string Name(AccountId? id) => id is { } a && names.TryGetValue(a, out var name) ? name : "";
        var bills = data.Bills();
        var today = DateOnly.FromDateTime(exportedAt.DateTime);

        var document = new ExportDocument("mossmint-export", FormatVersion, exportedAt, profileName, data.Settings(),
            [.. accounts.Select(a => new ExportedAccount(a, data.Balances(a.Id), data.Transactions(a.Id)))],
            bills, data.Income(), data.Debts(), data.Goals());

        var transactions = accounts.SelectMany(a => data.Transactions(a.Id))
            .OrderBy(t => t.EffectiveDate).ThenBy(t => Name(t.Account), StringComparer.Ordinal).ThenBy(t => t.Description, StringComparer.Ordinal)
            .Select(t => new[]
            {
                Date(t.EffectiveDate), Date(t.PostedDate), Text(Name(t.Account)), Text(t.Description), Text(t.Category),
                Number(t.Amount), t.Amount.Currency.Code, t.Status.ToString(), t.Provenance.Source.ToString(),
            });

        var billRows = bills.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => new[]
        {
            Text(b.Name), b.Purpose.ToString(), Number(b.Amount.Expected), Number(b.Amount.Low), Number(b.Amount.High), b.Amount.Kind.ToString(),
            b.Amount.Expected?.Currency.Code ?? "", Repeats(b.DueDates.Rule), Date(b.DueDates.StartsOn), Date(b.DueDates.EndsOn),
            Text(Name(b.FundingAccount)), b.IsAutopay ? "yes" : "no",
        });

        var debtRows = data.Debts().OrderBy(d => Name(d.Account), StringComparer.Ordinal).Select(d =>
        {
            var latest = data.Balances(d.Account).FirstOrDefault();
            var bill = bills.FirstOrDefault(b => b.TargetAccount == d.Account && b.Purpose == BillPurpose.DebtPayment);
            // A card's purchase rate stands for the card here; profile.json has every bucket.
            var (type, rates, payment) = d.Terms switch
            {
                SimpleInterestLoanTerms t => ("Loan", t.Rates, (Money?)t.RegularPayment),
                AmortizingLoanTerms a => ("Loan", a.Rates, a.ScheduledPayment),
                CreditCardTerms c => ("Credit card", c.Buckets[0].Rates, null),
                _ => ("Debt", (RateSchedule?)null, (Money?)null),
            };
            return new[]
            {
                Text(Name(d.Account)), type, Number(latest?.Balance), Date(latest?.Provenance.AsOf), latest?.Balance.Currency.Code ?? "",
                rates?.SegmentOn(today) is { } rate ? Plain(rate.Rate.Percent) : "", Number(bill?.Amount.Expected ?? payment),
                Date(d.OpenedOn), Number(d.OriginalPrincipal),
            };
        });

        return
        [
            new("profile.json", JsonSerializer.Serialize(document, Json)),
            new("transactions.csv", Csv("Date,Posted,Account,Description,Category,Amount,Currency,Status,Source", transactions)),
            new("bills.csv", Csv("Name,Purpose,Amount,Low,High,Amount kind,Currency,Repeats,Starts,Ends,Paid from,Autopay", billRows)),
            new("debts.csv", Csv("Name,Type,Balance,Balance as of,Currency,APR %,Payment,Opened on,Original principal", debtRows)),
        ];
    }

    // The byte order mark is what makes Excel read a CSV as UTF-8.
    private static string Csv(string header, IEnumerable<string[]> rows)
    {
        var csv = new StringBuilder("﻿").Append(header).Append("\r\n");
        foreach (var row in rows)
        {
            csv.AppendJoin(',', row).Append("\r\n");
        }

        return csv.ToString();
    }

    // OWASP CSV injection: a spreadsheet runs a cell starting with = + - @ tab or CR
    // as a formula; a leading apostrophe keeps it text. Only free text is escaped,
    // so an amount keeps its minus sign and stays a number.
    private static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private static string Number(Money? money) => money is { } m ? Plain(m.Amount) : "";

    // Exact, never rounded: trailing zeros dropped, then at least two decimals.
    private static string Plain(decimal value) => (value / 1.0000000000000000000000000000m + 0.00m).ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private static string Repeats(ScheduleRule rule) => rule switch
    {
        MonthlyRule m => $"monthly on {Day(m.Day)}",
        SemiMonthlyRule s => $"twice a month on {Day(s.First)} and {Day(s.Second)}",
        WeeklyRule { IntervalWeeks: 1 } w => $"weekly from {Date(w.Anchor)}",
        WeeklyRule w => $"every {w.IntervalWeeks} weeks from {Date(w.Anchor)}",
        _ => "custom",
    };

    private static string Day(DayOfMonth day) => day.IsLastDay ? "the last day" : $"day {day.Day}";

    private sealed record ExportDocument(
        string Format,
        int Version,
        DateTimeOffset ExportedAt,
        string Profile,
        PlanSettings Settings,
        IReadOnlyList<ExportedAccount> Accounts,
        IReadOnlyList<RecurringBill> Bills,
        IReadOnlyList<IncomeSchedule> Income,
        IReadOnlyList<DebtAccount> Debts,
        IReadOnlyList<SavingsGoal> Goals);

    private sealed record ExportedAccount(Account Account, IReadOnlyList<BalanceSnapshot> Balances, IReadOnlyList<Transaction> Transactions);
}
