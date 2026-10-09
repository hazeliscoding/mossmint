using System.Globalization;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Scheduling;
using Mossmint.Import.Excel.Xlsx;

namespace Mossmint.Import.Excel.Mapping;

// What the budget mapping needs to know about each debt workbook: its account
// and the payment it plans, to match the budget's payment lines.
internal sealed record DebtFacts(string AccountKey, string Name, Money? RegularPayment, Schedule? PaymentDates, SourceRef Source);

// The per-debt workbooks: a Settings sheet of labelled values, a Payment
// Schedule, and optionally a What If Scenario and a Payment Planner. Each
// becomes a liability account, its terms, and the balances it records.
internal static class DebtWorkbooks
{
    // The workbooks don't record a card's due date or minimum payment rule.
    // These stand in, flagged for review, until a statement confirms them.
    private const int AssumedDueDaysAfterClose = 21;
    private const decimal AssumedMinimumFloor = 40m;
    private const decimal AssumedMinimumFraction = 0.01m;

    public static DebtFacts? MapCard(MappingContext ctx, List<DebtFacts> debts)
    {
        var settings = new LabelledValues(ctx.Workbook.Sheet("Settings")!);
        var name = Labels.AccountNameFromFile(ctx.FileName);
        var key = AccountKey(name);
        var sheet = settings.Sheet;

        var balanceCell = settings.Value("Starting Balance", "Current Balance");
        var asOf = ctx.Date(settings.Value("Balance-as-of Date", "Balance As-Of Date"));
        var apr = ctx.Rate(sheet, settings.Value("APR", "Interest Rate"), $"{name} APR");
        var closingDay = MappingContext.Whole(settings.Value("Statement Closing Day"));
        var balance = ctx.Money(sheet, balanceCell, $"{name} balance");
        if (!Require(ctx, name, (balance, "the balance"), (asOf, "the balance-as-of date"), (apr, "the APR"), (closingDay is >= 1 and <= 31 ? closingDay : null, "the statement closing day")))
        {
            return null;
        }

        var payment = ctx.Money(sheet, settings.Value("Default Monthly Payment", "Monthly Payment"), $"{name} payment");
        var sources = new[] { ctx.Ref(sheet, settings.LabelCell("Statement Closing Day")!) };
        ctx.Output.Accounts.Add(new ProposedAccount(key, name, AccountKind.CreditCard, sources));
        var terms = new CreditCardTerms(
            DayOfMonth.Of(closingDay!.Value),
            PaymentDueRule.AfterClose(AssumedDueDaysAfterClose),
            [new AprBucket("purchase", BalanceBucketKind.Purchase, RateSchedule.Fixed(apr!.Value, asOf!.Value))],
            new MinimumPaymentRule(new Money(AssumedMinimumFloor, ctx.Currency), AssumedMinimumFraction, PlusInterestAndFees: true));
        ctx.Output.Debts.Add(new ProposedDebt(key, terms, sources));
        ctx.Output.Review("card-terms-assumed", $"{name}: due date and minimum payment aren't in the workbook",
            $"Mossmint assumes the payment is due {AssumedDueDaysAfterClose} days after the statement closes and the minimum is the larger of " +
            $"{ImportText.Amount(new Money(AssumedMinimumFloor, ctx.Currency))} or 1% of the balance plus interest. Check both on a statement.",
            sources, key);
        CheckDailyRate(ctx, settings, apr.Value, name);

        var accrued = ctx.Money(sheet, settings.Value("Interest Accrued Through As-of Date"), $"{name} accrued interest");
        ctx.Output.Balances.Add(new ProposedBalance(key, balance!.Value, asOf!.Value, "Balance on the as-of date", ctx.Ref(sheet, balanceCell!),
            accrued is { IsZero: false } ? accrued : null));

        var table = HeaderTable.Find(ctx.Workbook.Sheet("What If Scenario"), "Payment Date", "Balance After Interest", "Payment Used")
            ?? HeaderTable.Find(ctx.Workbook.Sheet("Payment Schedule"), "Payment Date", "Balance After Interest", "Payment Used");
        Reconcile(ctx, settings, key, name, "Actual Balance Override", null, table, (row, t) =>
            ctx.Money(t.Sheet, t.Cell(row, "Balance After Interest"), $"{name} projection") is { } after &&
            ctx.Money(t.Sheet, t.Cell(row, "Payment Used"), $"{name} projection") is { } used
                ? after - used
                : null);

        ReadPlanner(ctx, name, payment);
        RawSettings(ctx, settings);
        var schedule = new Schedule(new MonthlyRule(DayOfMonth.Last));
        var facts = new DebtFacts(key, name, payment, schedule, sources[0]);
        debts.Add(facts);
        return facts;
    }

    public static DebtFacts? MapSimpleInterestLoan(MappingContext ctx, List<DebtFacts> debts)
    {
        var settings = new LabelledValues(ctx.Workbook.Sheet("Settings")!);
        var name = Labels.AccountNameFromFile(ctx.FileName);
        var key = AccountKey(name);
        var sheet = settings.Sheet;

        var principalCell = settings.Value("Current Principal Balance", "Principal Balance");
        var principal = ctx.Money(sheet, principalCell, $"{name} principal");
        var asOf = ctx.Date(settings.Value("Balance As-Of Date", "Balance-as-of Date"));
        var rate = ctx.Rate(sheet, settings.Value("Interest Rate", "APR"), $"{name} interest rate");
        var payment = ctx.Money(sheet, settings.Value("Regular Payment", "Monthly Payment"), $"{name} payment");
        var dueDay = MappingContext.Whole(settings.Value("Due Day"));
        var plannedDay = MappingContext.Whole(settings.Value("Planned Payment Day")) ?? dueDay;
        if (!Require(ctx, name, (principal, "the principal balance"), (asOf, "the balance as-of date"), (rate, "the interest rate"),
                (payment, "the regular payment"), (plannedDay is >= 1 and <= 31 ? plannedDay : null, "the payment day")))
        {
            return null;
        }

        var labelSource = ctx.Ref(sheet, settings.LabelCell("Planned Payment Day", "Due Day")!);
        var opened = ctx.Date(settings.Value("Origination Date"));
        var schedule = new Schedule(new MonthlyRule(DayOfMonth.Of(plannedDay!.Value)));
        var terms = new SimpleInterestLoanTerms(
            RateSchedule.Fixed(rate!.Value, opened is { } o && o <= asOf!.Value ? o : asOf!.Value),
            DayCountConvention.Actual365Fixed,
            payment!.Value,
            schedule);
        ctx.Output.Accounts.Add(new ProposedAccount(key, name, AccountKind.InstallmentLoan, [labelSource]));
        ctx.Output.Debts.Add(new ProposedDebt(key, terms, [labelSource],
            ctx.Money(sheet, settings.Value("Original Loan Amount", "Original Loan"), $"{name} original amount"), opened));
        CheckDailyRate(ctx, settings, rate.Value, name);

        if (dueDay is { } due && due != plannedDay)
        {
            ctx.Output.Review("due-day-mismatch", $"{name}: planned payment day differs from the due day",
                $"The loan is due on day {due} of the month, but the workbook plans payments on day {plannedDay}. " +
                "A payment after the due date can be late and cost a fee. Mossmint follows the workbook's plan until you change it.",
                [ctx.Ref(sheet, settings.Value("Due Day")!), ctx.Ref(sheet, settings.Value("Planned Payment Day")!)], key);
        }

        var accrued = ctx.Money(sheet, settings.Value("Unpaid Accrued Interest"), $"{name} accrued interest");
        ctx.Output.Balances.Add(new ProposedBalance(key, principal!.Value, asOf!.Value, "Principal on the as-of date", ctx.Ref(sheet, principalCell!), accrued));

        var table = HeaderTable.Find(ctx.Workbook.Sheet("Payment Schedule"), "Payment Date", "Beginning Principal", "Principal Paid");
        Reconcile(ctx, settings, key, name, "Actual Principal Balance", "Ending Accrued Interest", table, (row, t) =>
            ctx.Money(t.Sheet, t.Cell(row, "Beginning Principal"), $"{name} projection") is { } begin &&
            ctx.Money(t.Sheet, t.Cell(row, "Principal Paid"), $"{name} projection") is { } paid
                ? begin - paid
                : null);

        if (ctx.Date(settings.Value("Current Estimated Payoff Date")) is { } lenderPayoff)
        {
            ctx.Output.Note("lender-payoff-estimate", $"{name}: lender's payoff estimate",
                $"The workbook records the lender's estimate of {ImportText.Date(lenderPayoff)}. Mossmint makes its own projection from the balance and terms.",
                [ctx.Ref(sheet, settings.Value("Current Estimated Payoff Date")!)], key);
        }

        ReadPlanner(ctx, name, payment);
        RawSettings(ctx, settings);
        var facts = new DebtFacts(key, name, payment, schedule, labelSource);
        debts.Add(facts);
        return facts;
    }

    public static DebtFacts? MapAutoLoan(MappingContext ctx, List<DebtFacts> debts)
    {
        var settings = new LabelledValues(ctx.Workbook.Sheet("Settings")!);
        var name = Labels.AccountNameFromFile(ctx.FileName);
        var key = AccountKey(name);
        var sheet = settings.Sheet;

        var balanceCell = settings.Value("Starting Balance", "Current Balance");
        var balance = ctx.Money(sheet, balanceCell, $"{name} balance");
        var asOf = ctx.Date(settings.Value("Baseline Date", "Balance As-Of Date"));
        var apr = ctx.Rate(sheet, settings.Value("APR", "Interest Rate"), $"{name} APR");
        var payment = ctx.Money(sheet, settings.Value("Monthly Payment", "Regular Payment"), $"{name} payment");
        if (!Require(ctx, name, (balance, "the starting balance"), (asOf, "the baseline date"), (apr, "the APR"), (payment, "the monthly payment")))
        {
            return null;
        }

        var frequency = settings.Value("Payment Frequency")?.Text?.Trim();
        if (frequency is not null && !frequency.Equals("Monthly", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Output.Block("unsupported-frequency", $"{name}: payment frequency '{frequency}' isn't supported yet",
                "This workbook shape is read for monthly payments only.", [ctx.Ref(sheet, settings.Value("Payment Frequency")!)], AccountKey(name));
            return null;
        }

        var table = HeaderTable.Find(ctx.Workbook.Sheet("Payment Schedule"), "Payment Date", "Beginning Balance", "Ending Balance");
        var paymentDays = table is null
            ? []
            : table.Rows("Payment Date").Select(r => ctx.Date(table.Cell(r, "Payment Date"))).OfType<DateOnly>().Where(d => d > asOf).ToList();
        var day = paymentDays.Count > 0 && paymentDays.All(d => d.Day == DateTime.DaysInMonth(d.Year, d.Month))
            ? DayOfMonth.Last
            : DayOfMonth.Of(paymentDays.FirstOrDefault(asOf!.Value).Day);

        var source = ctx.Ref(sheet, settings.LabelCell("Monthly Payment", "Regular Payment")!);
        var schedule = new Schedule(new MonthlyRule(day));
        var terms = new SimpleInterestLoanTerms(RateSchedule.Fixed(apr!.Value, asOf!.Value), DayCountConvention.Actual365Fixed, payment!.Value, schedule);
        ctx.Output.Accounts.Add(new ProposedAccount(key, name, AccountKind.InstallmentLoan, [source]));
        ctx.Output.Debts.Add(new ProposedDebt(key, terms, [source], ctx.Money(sheet, settings.Value("Original Loan", "Original Loan Amount"), $"{name} original amount")));
        CheckDailyRate(ctx, settings, apr.Value, name);
        ctx.Output.Balances.Add(new ProposedBalance(key, balance!.Value, asOf.Value, "Balance on the baseline date", ctx.Ref(sheet, balanceCell!)));

        if (MappingContext.Whole(settings.Value("Payments Completed Before Baseline")) is { } made && MappingContext.Whole(settings.Value("Original Loan Term")) is { } term)
        {
            ctx.Output.Note("loan-term", $"{name}: {made} of {term} payments made before the baseline",
                "The workbook's payment count is kept with the import for reference.", [ctx.Ref(sheet, settings.Value("Original Loan Term")!)], key);
        }

        Reconcile(ctx, settings, key, name, "Actual Balance Override", null, table, (row, t) =>
            ctx.Money(t.Sheet, t.Cell(row, "Beginning Balance"), $"{name} projection") is { } begin &&
            ctx.Money(t.Sheet, t.Cell(row, "Total Principal", "Principal"), $"{name} projection") is { } paid
                ? begin - paid
                : null);

        RawSettings(ctx, settings);
        var facts = new DebtFacts(key, name, payment, schedule, source);
        debts.Add(facts);
        return facts;
    }

    public static string AccountKey(string name) => $"account:{Labels.Slug(name)}";

    // A balance typed over the projection ("override" on a reconciled payment
    // date) is the user's record of what the lender reported after that
    // payment. It is imported as its own balance, and if it differs from what
    // the workbook itself projected, the gap goes to review. The workbooks'
    // own "Reconciliation Difference" can't show this: their projection
    // already uses the override.
    private static void Reconcile(MappingContext ctx, LabelledValues settings, string key, string name, string overrideLabel, string? accruedHeader,
        HeaderTable? table, Func<int, HeaderTable, Money?> projectedEnding)
    {
        var sheet = settings.Sheet;
        var actualCell = settings.Value(overrideLabel);
        var actual = ctx.Money(sheet, actualCell, $"{name} reconciled balance");
        var date = ctx.Date(settings.Value("Reconciled Payment Date"));
        if (actual is null || date is null)
        {
            return;
        }

        var row = table?.Rows("Payment Date").FirstOrDefault(r => ctx.Date(table.Cell(r, "Payment Date")) == date);
        var accrued = row is > 0 && accruedHeader is not null ? ctx.Money(table!.Sheet, table.Cell(row.Value, accruedHeader), $"{name} accrued interest") : null;
        ctx.Output.Balances.Add(new ProposedBalance(key, actual.Value, date.Value, $"Balance after the {ImportText.ShortDate(date.Value)} payment",
            ctx.Ref(sheet, actualCell!), accrued));

        if (row is not > 0)
        {
            ctx.Output.Note("reconcile-no-row", $"{name}: no schedule row for the reconciled date",
                $"The workbook has no payment on {ImportText.Date(date.Value)}, so the recorded balance couldn't be compared with a projection.",
                [ctx.Ref(sheet, settings.Value("Reconciled Payment Date")!)], key);
            return;
        }

        if (projectedEnding(row.Value, table!) is not { } projected || projected == actual.Value)
        {
            return;
        }

        var gap = actual.Value - projected;
        ctx.Output.Review("balance-mismatch", $"{name}: balance differs from the workbook's projection",
            $"After the payment on {ImportText.Date(date.Value)} the workbook projected {ImportText.Amount(projected)}, but the balance recorded is " +
            $"{ImportText.Amount(actual.Value)} ({ImportText.Amount(gap.IsNegative ? -gap : gap)} {(gap.IsPositive ? "higher" : "lower")}). " +
            "Mossmint keeps both and plans from the recorded balance. A payment that hadn't posted, a fee, or a different interest calculation can explain the gap.",
            [ctx.Ref(sheet, actualCell!), ctx.Ref(table!.Sheet, table.Cell(row.Value, "Payment Date")!)], key);
    }

    // A cached daily rate that no longer equals APR / 365 means the file was
    // saved without recalculating. Nothing reads the cached rate; it is a sign
    // other cached values may be stale too.
    private static void CheckDailyRate(MappingContext ctx, LabelledValues settings, AnnualRate rate, string name)
    {
        var cell = settings.Value("Daily Interest Rate", "Daily Rate");
        if (cell?.Number is { } cached && Math.Abs(cached - (rate.Value / 365m)) > 0.0000001m)
        {
            ctx.Output.Review("stale-cache", $"{name}: the workbook's saved values look out of date",
                $"The saved daily rate is {cached.ToString(CultureInfo.InvariantCulture)} but the rate divided by 365 is {(rate.Value / 365m).ToString("0.##########", CultureInfo.InvariantCulture)}. " +
                "Open the workbook, let it recalculate, save it, and import it again.",
                [ctx.Ref(settings.Sheet, cell)]);
        }
    }

    // Planner rows are the user's own plan. Past rows are what they paid; a
    // future row that differs from the regular payment is noted, since the
    // forecast follows the budget's payment lines.
    private static void ReadPlanner(MappingContext ctx, string name, Money? regular)
    {
        var table = HeaderTable.Find(ctx.Workbook.Sheet("Payment Planner"), "Payment Date", "Planned Payment");
        if (table is null)
        {
            return;
        }

        var differing = new List<string>();
        foreach (var row in table.Rows("Payment Date"))
        {
            var dateCell = table.Cell(row, "Payment Date")!;
            var date = ctx.Date(dateCell);
            var amount = ctx.Money(table.Sheet, table.Cell(row, "Planned Payment"), $"{name} planned payment");
            ctx.Raw("debt-planner-row", table.Sheet, dateCell, ("date", date?.ToString("yyyy-MM-dd")), ("amount", amount?.Amount.ToString(CultureInfo.InvariantCulture)));
            if (date >= ctx.Today && amount is { IsPositive: true } a && regular is { } r && a != r)
            {
                differing.Add($"{ImportText.ShortDate(date.Value)}: {ImportText.Amount(a)}");
            }
        }

        if (differing.Count > 0)
        {
            ctx.Output.Note("planner-differs", $"{name}: planned payments that differ from the regular payment",
                $"{string.Join("; ", differing)}. The forecast uses the budget's payment lines; adjust them if these plans still stand.",
                [ctx.Ref(table.Sheet, "A1")]);
        }
    }

    private static void RawSettings(MappingContext ctx, LabelledValues settings)
    {
        foreach (var label in settings.Sheet.Column(1).Where(c => c.Kind == CellKind.Text))
        {
            var value = settings.Value(label.Value);
            ctx.Raw("debt-setting", settings.Sheet, label, ("label", label.Value.Trim()), ("value", value?.Value), ("formula", value?.Formula));
        }
    }

    private static bool Require(MappingContext ctx, string name, params (object? Value, string What)[] fields)
    {
        var missing = fields.Where(f => f.Value is null).Select(f => f.What).ToList();
        if (missing.Count == 0)
        {
            return true;
        }

        ctx.Output.Block("missing-value", $"{name}: required values are missing or unreadable",
            $"Couldn't read {string.Join(", ", missing)} on the Settings sheet.", [new SourceRef(ctx.FileName, "Settings")], AccountKey(name));
        return false;
    }
}
