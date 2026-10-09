using System.Globalization;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Samples.Workbooks;

public sealed record SyntheticWorkbook(string FileName, byte[] Content);

// Switches for the awkward things real workbooks do.
public sealed record WorkbookQuirks(
    bool StaleTotal = false,
    bool StaleDailyRate = false,
    bool PlainSerialDates = false,
    bool DuplicateLine = false)
{
    public static WorkbookQuirks None { get; } = new();

    public static WorkbookQuirks All { get; } = new(true, true, true, true);
}

// The five workbook shapes the importer reads, built in code with invented
// figures for the Juniper sample household: a budget workbook and four debt
// workbooks (two cards, a simple-interest loan, an auto loan). The layouts
// follow the shapes the importer knows; every number here is made up, and the
// formulas' cached values are computed the way the sheets compute them.
public static class SyntheticWorkbooks
{
    public static readonly DateOnly Today = new(2026, 10, 9);

    // Gaps between a recorded balance and the workbook's own projection, so
    // tests can check the reconciliation against known amounts.
    public const decimal SorrelGap = 37.15m;
    public const decimal PineGap = 455.00m;

    private const string Spent = "FF00FFFF";
    private const string Added = "FF9900FF";
    private const string Header = "FFFF9900";
    private const string Totals = "FFFFFF00";
    private const string LeftOver = "FF00FF00";
    private const string MoneyFormat = "\"$\"#,##0.00";
    private const string LongDate = "mmmm\" \"d\", \"yyyy";

    private static readonly IBusinessCalendar Bank = CalendarSpec.UsFederalReserve.Build();
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static IReadOnlyList<SyntheticWorkbook> All(WorkbookQuirks? quirks = null)
    {
        quirks ??= WorkbookQuirks.None;
        return [Budget(quirks), FernCard(quirks), SorrelCard(quirks), BrambleLoan(quirks), PineAuto(quirks)];
    }

    public static SyntheticWorkbook FernCard(WorkbookQuirks quirks) =>
        Card("Fern Card Workbook.xlsx", quirks, start: 2400.00m, asOf: new(2026, 8, 5), apr: 0.2499m, payment: 300m, closingDay: 10,
            planner: [(new(2026, 8, 31), 300m), (new(2026, 9, 30), 300m)], gap: 0m);

    public static SyntheticWorkbook SorrelCard(WorkbookQuirks quirks) =>
        Card("Sorrel Card Workbook.xlsx", quirks, start: 1250.00m, asOf: new(2026, 7, 31), apr: 0.1899m, payment: 150m, closingDay: 25,
            planner: [(new(2026, 8, 31), 150m), (new(2026, 9, 30), 120m), (new(2026, 11, 30), 250m)], gap: SorrelGap);

    public static SyntheticWorkbook BrambleLoan(WorkbookQuirks quirks)
    {
        var book = new XlsxWriter();
        var settings = book.Sheet("Settings");
        var rate = 0.1650m;
        var daily = rate / 365m;
        var asOf = new DateOnly(2026, 8, 5);
        decimal principal = 6200.00m, accrued = 31.20m, regular = 280.00m;
        var first = new DateOnly(2026, 8, 12);

        var schedule = book.Sheet("Payment Schedule");
        string[] headers = ["Payment", "Payment Date", "Days", "Beginning Principal", "Beginning Accrued Interest", "New Interest", "Total Interest Due",
            "Payment", "Interest Paid", "Principal Paid", "Ending Principal", "Ending Accrued Interest", "Total Amount Owed"];
        Headers(schedule, 1, headers);
        var previous = asOf;
        var reconciledOn = new DateOnly(2026, 9, 12);
        decimal reconciledPrincipal = 0m;
        for (var i = 0; i < 12; i++)
        {
            var row = 2 + i;
            var date = first.AddMonths(i);
            var days = date.DayNumber - previous.DayNumber;
            var newInterest = Round(principal * daily * days);
            var due = accrued + newInterest;
            var pay = Math.Min(regular, principal + due);
            var interestPaid = Math.Min(pay, due);
            var principalPaid = Math.Max(Round(pay - interestPaid), 0m);
            var ending = Math.Max(Round(principal - principalPaid), 0m);
            var endingAccrued = Math.Max(Round(due - interestPaid), 0m);
            schedule.Set($"A{row}", 20 + i, i == 0 ? "=20" : $"=A{row - 1}+1")
                .Set($"B{row}", date, i == 0 ? "=DATE(2026,8,12)" : $"=EDATE(B{row - 1},1)", "mm-dd-yy")
                .Set($"C{row}", days, i == 0 ? "=B2-Settings!$B$7" : $"=B{row}-B{row - 1}")
                .Set($"D{row}", principal, null, MoneyFormat).Set($"E{row}", accrued, null, MoneyFormat)
                .Set($"F{row}", newInterest, $"=ROUND(D{row}*Settings!$B$5*C{row},2)", MoneyFormat)
                .Set($"G{row}", due, null, MoneyFormat).Set($"H{row}", pay, null, MoneyFormat)
                .Set($"I{row}", interestPaid, null, MoneyFormat).Set($"J{row}", principalPaid, null, MoneyFormat)
                .Set($"K{row}", ending, null, MoneyFormat).Set($"L{row}", endingAccrued, null, MoneyFormat)
                .Set($"M{row}", ending + endingAccrued, null, MoneyFormat);
            if (date == reconciledOn)
            {
                reconciledPrincipal = ending;
            }

            (principal, accrued, previous) = (ending, endingAccrued, date);
        }

        Labelled(settings, quirks,
            ("Original Loan Amount", 9000.00m, null, MoneyFormat),
            ("Current Principal Balance", 6200.00m, null, MoneyFormat),
            ("Unpaid Accrued Interest ", 31.20m, null, MoneyFormat),
            ("Interest Rate", rate, null, "0.00%"),
            ("Daily Interest Rate", quirks.StaleDailyRate ? 0.000412m : daily, "=B4/365", "0.000%"),
            ("Regular Payment", regular, null, null),
            ("Balance As-Of Date", asOf, null, null),
            ("Due Day", 5m, null, null),
            ("Planned Payment Day", 12m, null, null),
            ("Origination Date", new DateOnly(2024, 6, 5), null, LongDate),
            ("Current Estimated Payoff Date", new DateOnly(2028, 12, 5), null, LongDate),
            ("Actual Principal Balance ", reconciledPrincipal, null, MoneyFormat),
            ("Reconciled Payment Date", reconciledOn, null, null),
            ("Reconciliation Difference", 0m, "=B12-'Payment Schedule'!K3", MoneyFormat));

        Planner(book.Sheet("Payment Planner"), quirks, [(first, regular), (first.AddMonths(1), regular)], withStatus: true);
        book.Sheet("Dashboard").Set("A1", "Payoff Date").Set("B1", new DateOnly(2028, 11, 12), "=IFERROR(__xludf.DUMMYFUNCTION(\"FILTER(A1:A9,B1:B9>0)\"),45973)", LongDate);
        BudgetExport(book.Sheet("Budget Export"));
        return new SyntheticWorkbook("Bramble Loan Workbook.xlsx", book.ToBytes());
    }

    public static SyntheticWorkbook PineAuto(WorkbookQuirks quirks)
    {
        var book = new XlsxWriter();
        var settings = book.Sheet("Settings");
        var apr = 0.0599m;
        var daily = apr / 365m;
        var baseline = new DateOnly(2026, 7, 31);
        decimal balance = 15800.00m, payment = 455.00m;

        var schedule = book.Sheet("Payment Schedule");
        Headers(schedule, 1, ["Payment #", "Payment Date", "Days", "Beginning Balance", "Interest", "Principal", "Extra Principle", "Total Principal", "Ending Balance"]);
        schedule.Set("A2", 20m).Set("B2", baseline, null, "mm/dd/yyyy").Set("C2", 0m).Set("D2", balance, null, MoneyFormat)
            .Set("E2", 0m).Set("F2", 0m).Set("H2", 0m).Set("I2", balance, null, MoneyFormat);
        var previous = baseline;
        var reconciledOn = new DateOnly(2026, 9, 30);
        decimal projected = 0m;
        for (var i = 1; i <= 12; i++)
        {
            var row = 2 + i;
            var date = EndOfMonth(baseline.AddMonths(i));
            var days = date.DayNumber - previous.DayNumber;
            var interest = Round(balance * daily * days);
            var principal = Math.Min(payment - interest, balance);
            var ending = Round(balance - principal);
            schedule.Set($"A{row}", 20 + i, $"=A{row - 1}+1").Set($"B{row}", date, $"=EOMONTH(B{row - 1},1)", "mm-dd-yy")
                .Set($"C{row}", days, $"=B{row}-B{row - 1}").Set($"D{row}", balance, $"=I{row - 1}", MoneyFormat)
                .Set($"E{row}", interest, $"=ROUND(D{row}*Settings!$B$4*C{row},2)", MoneyFormat)
                .Set($"F{row}", principal, null, MoneyFormat).Set($"H{row}", principal, $"=F{row}+G{row}", MoneyFormat)
                .Set($"I{row}", ending, null, MoneyFormat);
            if (date == reconciledOn)
            {
                projected = ending;
            }

            (balance, previous) = (ending, date);
        }

        Labelled(settings, quirks,
            ("Original Loan", 24000.00m, null, MoneyFormat),
            ("Starting Balance", 15800.00m, null, MoneyFormat),
            ("APR", apr, null, "0.00%"),
            ("Daily Interest Rate", quirks.StaleDailyRate ? 0.0002m : daily, "=B3/365", "0.00%"),
            ("Monthly Payment", payment, null, null),
            ("Baseline Date", baseline, null, null),
            ("Payment Frequency", "Monthly", null, null),
            ("Payments Completed Before Baseline", 20m, null, null),
            ("Original Loan Term", 60m, null, null),
            ("Actual Balance Override", projected + PineGap, null, MoneyFormat),
            ("Reconciled Payment Date", reconciledOn, null, null),
            ("Reconciliation Difference", PineGap, "=B10-'Payment Schedule'!I4", MoneyFormat));

        book.Sheet("Dashboard").Set("A1", "Estimated Payoff").Set("B1", new DateOnly(2029, 12, 31), null, LongDate);
        book.Sheet("What If Scenario").Set("A1", "Extra Payment").Set("B1", 0m);
        BudgetExport(book.Sheet("Budget Export"));
        return new SyntheticWorkbook("Pine Auto Workbook.xlsx", book.ToBytes());
    }

    public static SyntheticWorkbook Budget(WorkbookQuirks quirks)
    {
        var book = new XlsxWriter();
        var main = book.Sheet("Main Money");
        main.Set("A1", "What each check pays for").Set("D1", "Blue = Money Spent", fill: Spent).Set("D2", "Purple = Money Added", fill: Added);

        var row = 4;
        var carry = 0m;
        var first = true;
        var month = new DateOnly(2026, 7, 1);
        var leftOverRows = new List<(DateOnly Date, int Row, string Column)>();
        for (var m = 0; m < 9; m++, month = month.AddMonths(1))
        {
            if (month.Month == 1)
            {
                main.Set($"A{row}", (decimal)month.Year, fill: "FFB4A7D6");
                row += 4;
            }

            var mid = PayDate(month, DayOfMonth.Of(15));
            var end = PayDate(month, DayOfMonth.Last);
            var oldFormat = month.Month == 7;
            var midLines = MidLines(month, quirks);
            var endLines = EndLines(month);
            var endPay = month == new DateOnly(2026, 12, 1) ? 1700.00m : 1580.00m;
            main.Set($"A{row}", Label(month, mid), fill: Header).Set($"B{row}", 1600.00m, null, "#,##0.00", Header)
                .Set($"D{row}", Label(month, end), fill: Header).Set($"E{row}", endPay, null, "#,##0.00", Header);
            if (month == new DateOnly(2026, 11, 1))
            {
                main.Set($"F{row}", "****Insurance renews in December - what's shown is an estimate");
            }

            var lineRow = row + 2;
            var count = Math.Max(midLines.Count, endLines.Count);
            for (var i = 0; i < count; i++)
            {
                WriteLine(main, "A", lineRow + i, i < midLines.Count ? midLines[i] : null, mid, oldFormat);
                WriteLine(main, "D", lineRow + i, i < endLines.Count ? endLines[i] : null, end, oldFormat);
            }

            var totalRow = lineRow + count + 1;
            var leftRow = totalRow + 1;
            var range = (Start: lineRow, End: lineRow + count - 1);
            var midPaid = midLines.Where(l => Paid(l, mid, oldFormat)).Sum(l => l.Amount);
            var endPaid = endLines.Where(l => Paid(l, end, oldFormat)).Sum(l => l.Amount);
            var midTotal = quirks.StaleTotal && month == new DateOnly(2026, 9, 1) ? midPaid + 10m : midPaid;
            main.Set($"A{totalRow}", "Total Paid", fill: Totals).Set($"D{totalRow}", "Total Paid", fill: Totals);
            main.Set($"B{totalRow}", midTotal, oldFormat ? $"=SUM(B{range.Start}:B{range.End})" : $"=SUMIF(C{range.Start}:C{range.End},TRUE,B{range.Start}:B{range.End})", "#,##0.00", Totals);
            main.Set($"E{totalRow}", endPaid, oldFormat ? $"=SUM(E{range.Start}:E{range.End})" : $"=SUMIF(F{range.Start}:F{range.End},TRUE,E{range.Start}:E{range.End})", "#,##0.00", Totals);

            var adjustment = first ? 12.50m : 0m;
            var midLeft = 1600.00m - midPaid + carry + adjustment;
            var endLeft = endPay - endPaid + midLeft;
            main.Set($"A{leftRow}", "Current Left Over", fill: LeftOver).Set($"D{leftRow}", "Current Left Over", fill: LeftOver)
                .Set($"B{leftRow}", midLeft, first ? $"=((B{row}-B{totalRow})+12.50)" : $"=((B{row}-B{totalRow})+{leftOverRows[^1].Column}{leftOverRows[^1].Row})", "#,##0.00", LeftOver)
                .Set($"E{leftRow}", endLeft, $"=((E{row}-E{totalRow})+B{leftRow})", "#,##0.00", LeftOver);
            if (!oldFormat)
            {
                Date(main, $"AB{leftRow}", mid, $"=DATE({mid.Year},{mid.Month},{mid.Day})", quirks);
                Date(main, $"AC{leftRow}", end, $"=DATE({end.Year},{end.Month},{end.Day})", quirks);
            }

            leftOverRows.Add((end, leftRow, "E"));
            carry = endLeft;
            first = false;
            row = leftRow + 3;
        }

        var dashboard = book.Sheet("Debt Dashboard");
        dashboard.Set("A1", "PORTFOLIO SUMMARY").Set("A2", "Next Debt Paid Off").Set("A3", "Sorrel", "=IFERROR(__xludf.DUMMYFUNCTION(\"INDEX(FILTER(J:J,L:L>0),1)\"),\"Sorrel\")")
            .Set("A34", "Minimum Reserve").Set("C34", 150m, null, MoneyFormat);

        var savings = book.Sheet("Savings Tracker");
        Headers(savings, 1, ["Paycheck Date", "Check Type", "Savings Goal", "Left Over", "Auto Savings", "Shortfall", "Excess After Savings", "Month", "Year ", "Running Savings", "Confirmed Saved"]);
        var running = 0m;
        var r = 2;
        for (var month2 = new DateOnly(2026, 8, 1); month2 <= new DateOnly(2027, 3, 1); month2 = month2.AddMonths(1))
        {
            foreach (var (date, type) in new[] { (PayDate(month2, DayOfMonth.Of(15)), "15th"), (PayDate(month2, DayOfMonth.Last), "End Month") })
            {
                var saved = date <= Today ? 100m : 0m;
                running += saved;
                savings.Set($"A{r}", date, null, LongDate).Set($"B{r}", type).Set($"C{r}", 100m, "=100")
                    .Set($"J{r}", running, $"=SUM($E$2:E{r})").Set($"K{r}", 0m);
                if (date <= Today)
                {
                    savings.Set($"E{r}", saved);
                }

                r++;
            }
        }

        foreach (var name in new[] { "Fern Import", "Sorrel Import" })
        {
            var hidden = book.Sheet(name, hidden: true);
            hidden.Set("A1", "Year", "=IFERROR(__xludf.DUMMYFUNCTION(\"IMPORTRANGE(\"\"sheet-id\"\",\"\"Budget Export!A1:L100\"\")\"),\"Year\")");
            hidden.Set("A2", 2026m, "=IFERROR(__xludf.DUMMYFUNCTION(\"\"\"COMPUTED_VALUE\"\"\"),2026)");
        }

        return new SyntheticWorkbook("Juniper Budget.xlsx", book.ToBytes());
    }

    private sealed record BudgetLine(string Label, decimal Amount, bool AddsMoney = false);

    private static List<BudgetLine> MidLines(DateOnly month, WorkbookQuirks quirks)
    {
        var lines = new List<BudgetLine> { new("Rent", 950.00m), new("Bramble Loan", 280.00m), new("Phone", 45.00m) };
        if (month == new DateOnly(2026, 8, 1))
        {
            lines.Add(new("Takeout", 32.40m));
            lines.Add(new("Refund", 18.00m, AddsMoney: true));
        }

        if (quirks.DuplicateLine && month == new DateOnly(2026, 12, 1))
        {
            lines.Add(new("Coffee", 12.00m));
            lines.Add(new("Coffee", 9.50m));
        }

        return lines;
    }

    private static List<BudgetLine> EndLines(DateOnly month)
    {
        var lines = new List<BudgetLine> { new("Pine Auto", 455.00m) };
        if (month <= new DateOnly(2027, 1, 1))
        {
            lines.Add(new("Fern Card", 300.00m));
        }

        lines.Add(new("Sorrel Card", 150.00m));
        lines.Add(new("Groceries", month.Month switch { 10 => 215.00m, 12 => 230.00m, _ => 220.00m }));
        if (month.Month % 2 == 0)
        {
            lines.Add(new("Insurance", 96.00m));
        }

        if (month == new DateOnly(2026, 11, 1))
        {
            lines.Add(new("Dentist", 140.00m));
        }

        return lines;
    }

    private static void WriteLine(XlsxWriter.SheetBuilder sheet, string column, int row, BudgetLine? line, DateOnly payDate, bool oldFormat)
    {
        if (line is null)
        {
            return;
        }

        var amountColumn = (char)(column[0] + 1);
        var flagColumn = (char)(column[0] + 2);
        if (oldFormat)
        {
            var fill = line.AddsMoney ? Added : Spent;
            sheet.Set($"{column}{row}", line.Label, fill: fill).Set($"{amountColumn}{row}", line.Amount, null, "#,##0.00", fill);
            return;
        }

        var amount = line.AddsMoney ? -line.Amount : line.Amount;
        sheet.Set($"{column}{row}", line.Label, fill: line.AddsMoney ? Added : null)
            .Set($"{amountColumn}{row}", amount, null, "#,##0.00", line.AddsMoney ? Added : null)
            .Set($"{flagColumn}{row}", payDate < Today);
    }

    private static bool Paid(BudgetLine line, DateOnly payDate, bool oldFormat) => oldFormat || payDate < Today;

    private static SyntheticWorkbook Card(string fileName, WorkbookQuirks quirks, decimal start, DateOnly asOf, decimal apr, decimal payment, int closingDay,
        (DateOnly Date, decimal Amount)[] planner, decimal gap)
    {
        var book = new XlsxWriter();
        var daily = apr / 365m;
        var firstClose = new DateOnly(asOf.Year, asOf.Month, closingDay);
        if (asOf.Day > closingDay)
        {
            firstClose = firstClose.AddMonths(1);
        }

        var reconciledOn = new DateOnly(2026, 9, 30);
        var schedule = book.Sheet("Payment Schedule");
        var whatIf = book.Sheet("What If Scenario");
        string[] headers = ["Payment #", "Statement Close Date", "Payment Date", "Billing Cycle Days", "Beginning Balance", "Prior Pre-Payment Balance",
            "Days Before Prior Payment", "Days After Prior Payment", "Interest Charged", "Balance After Interest", "Planned Payment", "Payment Used",
            "Principal Reduction", "Ending Balance"];
        Headers(schedule, 14, headers);
        Headers(whatIf, 14, headers);
        var balance = start;
        var previousClose = asOf;
        decimal projected = 0m;
        for (var i = 0; i < 10 && balance > 0m; i++)
        {
            var row = 15 + i;
            var close = firstClose.AddMonths(i);
            var paymentDate = EndOfMonth(close);
            var days = close.DayNumber - previousClose.DayNumber;
            var interest = Round(balance * daily * days);
            var after = balance + interest;
            var planned = planner.FirstOrDefault(p => p.Date == paymentDate).Amount is var p && p > 0m ? p : payment;
            var used = Math.Min(planned, after);
            var ending = Round(after - used);
            foreach (var sheet in new[] { schedule, whatIf })
            {
                sheet.Set($"A{row}", i + 1).Set($"B{row}", close, null, LongDate).Set($"C{row}", paymentDate, $"=EOMONTH(B{row},0)", "mm-dd-yy")
                    .Set($"D{row}", days).Set($"E{row}", balance, null, MoneyFormat)
                    .Set($"I{row}", interest, null, MoneyFormat).Set($"J{row}", after, $"=ROUND(E{row}+I{row},2)", MoneyFormat)
                    .Set($"K{row}", sheet == whatIf ? planned : payment).Set($"L{row}", sheet == whatIf ? used : Math.Min(payment, after), null, MoneyFormat)
                    .Set($"M{row}", Math.Max(used - interest, 0m), null, MoneyFormat).Set($"N{row}", ending, null, MoneyFormat);
            }

            if (paymentDate == reconciledOn)
            {
                projected = ending;
            }

            (balance, previousClose) = (ending, close);
        }

        Labelled(book.Sheet("Settings"), quirks,
            ("Starting Balance", start, null, MoneyFormat),
            ("APR", apr, null, "0.00%"),
            ("Daily Interest Rate", quirks.StaleDailyRate ? daily * 2m : daily, "=B2/365", "0.00000%"),
            ("Default Monthly Payment", payment, null, null),
            ("Balance-as-of Date", asOf, null, LongDate),
            ("Statement Closing Day", closingDay, null, null),
            ("First Projected Closing Day", firstClose, "=DATE(YEAR(B5),MONTH(B5)+IF(DAY(B5)>B6,1,0),B6)", LongDate),
            ("First Projected Payment Date", EndOfMonth(firstClose), "=EOMONTH(B7,0)", LongDate),
            ("Interest Accrued Through As-of Date", 0m, null, null),
            ("Actual Balance Override", projected + gap, null, MoneyFormat),
            ("Reconciled Payment Date", reconciledOn, null, null),
            ("Reconciliation Difference", 0m, "=B10-'What If Scenario'!N16", MoneyFormat));
        whatIf.Set("A7", "Original Projected Payoff").Set("B7", new DateOnly(2027, 6, 30), "=IFERROR(__xludf.DUMMYFUNCTION(\"INDEX(FILTER('Payment Schedule'!$C$15:$C1000,'Payment Schedule'!$N$15:$N1000<=0),1)\"),46568)", LongDate);
        Planner(book.Sheet("Payment Planner"), quirks, planner, withStatus: false);
        book.Sheet("Dashboard").Set("A1", "Current Balance").Set("B1", projected + gap, null, MoneyFormat);
        BudgetExport(book.Sheet("Budget Export"));
        return new SyntheticWorkbook(fileName, book.ToBytes());
    }

    private static void Labelled(XlsxWriter.SheetBuilder sheet, WorkbookQuirks quirks, params (string Label, object Value, string? Formula, string? Format)[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var (label, value, formula, format) = rows[i];
            sheet.Set($"A{i + 1}", label);
            if (value is DateOnly date)
            {
                Date(sheet, $"B{i + 1}", date, formula, quirks, format);
            }
            else
            {
                sheet.Set($"B{i + 1}", value is int n ? (decimal)n : value, formula, format);
            }
        }
    }

    // With PlainSerialDates, dates are written as bare serial numbers with no
    // date format, the way they look after a careless copy and paste.
    private static void Date(XlsxWriter.SheetBuilder sheet, string address, DateOnly date, string? formula, WorkbookQuirks quirks, string? format = null)
    {
        if (quirks.PlainSerialDates)
        {
            sheet.Set(address, XlsxWriter.Serial(date, date1904: false), formula);
        }
        else
        {
            sheet.Set(address, date, formula, format ?? "m/d/yyyy");
        }
    }

    private static void Planner(XlsxWriter.SheetBuilder sheet, WorkbookQuirks quirks, (DateOnly Date, decimal Amount)[] rows, bool withStatus)
    {
        sheet.Set("A1", "Payment Date");
        if (withStatus)
        {
            sheet.Set("B1", "Status").Set("C1", "Planned Payment");
        }
        else
        {
            sheet.Set("B1", "Planned Payment");
        }

        for (var i = 0; i < rows.Length; i++)
        {
            Date(sheet, $"A{i + 2}", rows[i].Date, null, quirks);
            if (withStatus)
            {
                sheet.Set($"B{i + 2}", "Regular", "=IF(C2=\"\",\"Regular\",\"Extra Payment\")").Set($"C{i + 2}", rows[i].Amount);
            }
            else
            {
                sheet.Set($"B{i + 2}", rows[i].Amount);
            }
        }
    }

    private static void BudgetExport(XlsxWriter.SheetBuilder sheet) =>
        Headers(sheet, 1, ["Year", "Month", "Payment #", "Scheduled Date", "Payment Used", "Beginning Balance", "Ending Balance", "Extra Payment", "Status"]);

    private static void Headers(XlsxWriter.SheetBuilder sheet, int row, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Set($"{Column(i + 1)}{row}", headers[i]);
        }
    }

    private static string Column(int column)
    {
        var letters = "";
        for (var c = column; c > 0; c = (c - 1) / 26)
        {
            letters = (char)('A' + ((c - 1) % 26)) + letters;
        }

        return letters;
    }

    private static DateOnly PayDate(DateOnly month, DayOfMonth day) =>
        BusinessDays.Adjust(day.In(month.Year, month.Month), BusinessDayAdjustment.PreviousBusinessDay, Bank);

    // Labels are typed by hand: the month-end check keeps its nominal day.
    private static string Label(DateOnly month, DateOnly payDate)
    {
        var day = payDate.Day >= 28 ? DateTime.DaysInMonth(month.Year, month.Month) : payDate.Day;
        var suffix = (day % 10, day / 10 % 10) switch
        {
            (_, 1) => "th",
            (1, _) => "st",
            (2, _) => "nd",
            (3, _) => "rd",
            _ => "th",
        };
        return $"{month.ToString("MMMM", Us)} {day}{suffix} check";
    }

    private static DateOnly EndOfMonth(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
