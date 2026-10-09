using System.Globalization;
using System.Text.RegularExpressions;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Import.Excel.Xlsx;

namespace Mossmint.Import.Excel.Mapping;

// The budget workbook plans every paycheck in a block: a header with the
// check's net pay, the lines it pays (with a paid tick box), then Total Paid
// and Current Left Over. Two checks a month sit side by side. Upcoming blocks
// become income and bills; past blocks are kept as raw history; the newest
// past left-over becomes the spending account's starting figure.
internal static partial class BudgetWorkbook
{
    public const string AccountKey = "account:budget-spending";

    private static readonly IBusinessCalendar BankCalendar = CalendarSpec.UsFederalReserve.Build();

    public static void Map(MappingContext ctx, IReadOnlyList<DebtFacts> debts)
    {
        var sheet = ctx.Workbook.Sheet("Main Money")!;
        var legend = Legend.From(sheet);
        var blocks = FindBlocks(ctx, sheet, legend);
        if (blocks.Count == 0)
        {
            ctx.Output.Block("no-paychecks", "No paycheck blocks found",
                "The Main Money sheet has no rows like \"May 15th check\", so there is nothing to import.", [ctx.Ref(sheet, "A1")]);
            return;
        }

        foreach (var block in blocks)
        {
            foreach (var line in block.Lines)
            {
                ctx.Raw("budget-line", sheet, line.LabelCell ?? line.AmountCell!,
                    ("paycheck", block.PayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("side", block.Side.Key),
                    ("label", line.Label),
                    ("amount", line.Amount?.Amount.ToString(CultureInfo.InvariantCulture)),
                    ("paid", line.Paid?.ToString()),
                    ("kind", line.IsAdded ? "added" : "spent"),
                    ("fill", line.Fill));
            }
        }

        var sides = blocks.Select(b => b.Side).DistinctBy(s => s.Key).ToList();
        var upcoming = blocks.Where(b => b.PayDate >= ctx.Today).ToList();
        ctx.Output.Accounts.Add(new ProposedAccount(AccountKey, "Checking", AccountKind.Checking, [ctx.Ref(sheet, "A1")]));
        MapCash(ctx, sheet, blocks);
        foreach (var side in sides)
        {
            var sideBlocks = upcoming.Where(b => b.Side.Key == side.Key).OrderBy(b => b.PayDate).ToList();
            MapIncome(ctx, sheet, side, sideBlocks);
            MapBills(ctx, sheet, side, sideBlocks, sides.Count > 1 ? blocks : [], debts);
        }

        FlagUnpaidPastLines(ctx, sheet, blocks);
        FlagNotes(ctx, sheet, upcoming);
        CheckTotals(ctx, sheet, blocks);
        MapSavingsAndReserve(ctx);
        NoteGoogleOnly(ctx);
    }

    private static List<Block> FindBlocks(MappingContext ctx, XlsxSheet sheet, Legend legend)
    {
        var headers = sheet.Cells
            .Where(c => c.Kind == CellKind.Text && HeaderPattern().IsMatch(c.Value))
            .GroupBy(c => c.Row)
            .OrderBy(g => g.Key)
            .ToList();
        var blocks = new List<Block>();
        var rollovers = 0;
        int? previousMonth = null;
        var raw = new List<(int Row, int Month, int Rollover, List<XlsxCell> Headers)>();
        foreach (var row in headers)
        {
            var month = Month(row.First().Value);
            if (previousMonth is { } p && month < p)
            {
                rollovers++;
            }

            previousMonth = month;
            raw.Add((row.Key, month, rollovers, [.. row.OrderBy(c => c.Column)]));
        }

        var nextHeaderRow = raw.Skip(1).Select(r => r.Row).Append(sheet.LastRow + 1).ToList();
        var baseYear = BaseYear(ctx, sheet, raw.Select(r => (r.Row, r.Month, r.Rollover)).ToList(), nextHeaderRow);
        for (var i = 0; i < raw.Count; i++)
        {
            var (row, month, rollover, cells) = raw[i];
            var year = baseYear + rollover;
            for (var s = 0; s < cells.Count; s++)
            {
                var header = cells[s];
                var side = new Side(s == 0 ? "mid" : "end", s == 0 ? "15th" : "end of month", header.Column, DayOf(header.Value));
                var block = ReadBlock(ctx, sheet, legend, side, header, year, month, nextHeaderRow[i]);
                if (block is not null)
                {
                    blocks.Add(block);
                }
            }
        }

        return blocks;
    }

    // The sheet doesn't say which year its first block is in. A helper date on
    // a left-over row (a typed DATE(y,m,d)) anchors it; failing that, a year
    // row such as "2027" between blocks does; failing both, the current year.
    private static int BaseYear(MappingContext ctx, XlsxSheet sheet, List<(int Row, int Month, int Rollover)> headers, List<int> ends)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var (row, month, rollover) = headers[i];
            var anchor = sheet.Cells
                .Where(c => c.Row > row && c.Row < ends[i] && c.Column > 6)
                .Select(ctx.Date)
                .FirstOrDefault(d => d is { } date && date.Month == month);
            if (anchor is { } date)
            {
                return date.Year - rollover;
            }
        }

        foreach (var separator in sheet.Column(1).Where(c => c.Number is >= 2000m and < 2100m && !c.IsDateFormat))
        {
            var after = headers.FirstOrDefault(h => h.Row > separator.Row);
            if (after != default)
            {
                return (int)separator.Number!.Value - after.Rollover;
            }
        }

        ctx.Output.Review("budget-year-guessed", "The budget's year is a guess",
            $"Nothing in the Main Money sheet says which year its first paycheck is in, so Mossmint assumed {ctx.Today.Year}.", [ctx.Ref(sheet, "A1")]);
        return ctx.Today.Year;
    }

    private static Block? ReadBlock(MappingContext ctx, XlsxSheet sheet, Legend legend, Side side, XlsxCell header, int year, int month, int end)
    {
        var labelColumn = side.LabelColumn;
        var rows = Enumerable.Range(header.Row + 1, Math.Max(0, end - header.Row - 1)).ToList();
        var totalRow = rows.FirstOrDefault(r => Labels.Normalize(sheet.Cell(r, labelColumn)?.Text) == "total paid");
        var leftOverRow = rows.FirstOrDefault(r => Labels.Normalize(sheet.Cell(r, labelColumn)?.Text) == "current left over");
        if (totalRow == 0)
        {
            ctx.Output.Note("block-without-total", $"{header.Value.Trim()} has no Total Paid row", "Its lines are kept as history only.", [ctx.Ref(sheet, header)]);
            return null;
        }

        var payDate = HelperDate(ctx, sheet, side, header.Row, end, month)
            ?? BusinessDays.Adjust(side.Day.In(year, month), BusinessDayAdjustment.PreviousBusinessDay, BankCalendar);
        var lines = new List<Line>();
        foreach (var r in rows.Where(r => r < totalRow))
        {
            var labelCell = sheet.Cell(r, labelColumn);
            var amountCell = sheet.Cell(r, labelColumn + 1);
            var flagCell = sheet.Cell(r, labelColumn + 2);
            var label = labelCell?.Text?.Trim();
            if (string.IsNullOrEmpty(label) && amountCell?.Number is null)
            {
                continue;
            }

            var amount = ctx.Money(sheet, amountCell, $"{label ?? "A line"} on the {ImportText.ShortDate(payDate)} check");
            var fill = labelCell?.Fill ?? amountCell?.Fill;
            var added = (amount?.IsNegative ?? false) || (fill is not null && legend.IsAdded(fill));
            var paid = flagCell?.Boolean ?? (fill is not null && (legend.IsSpent(fill) || legend.IsAdded(fill)) ? true : null);
            lines.Add(new Line(label ?? "", amount, paid, added, fill, labelCell, amountCell));
        }

        return new Block(side, payDate, ctx.Money(sheet, sheet.Cell(header.Row, labelColumn + 1), header.Value.Trim()), header,
            lines, sheet.Cell(totalRow, labelColumn + 1), leftOverRow == 0 ? null : sheet.Cell(leftOverRow, labelColumn + 1));
    }

    // Left-over rows may carry the real pay dates in helper columns to the
    // right. They win over the label, which is typed by hand and sometimes off.
    private static DateOnly? HelperDate(MappingContext ctx, XlsxSheet sheet, Side side, int headerRow, int end, int month)
    {
        var dates = sheet.Cells
            .Where(c => c.Row > headerRow && c.Row < end && c.Column > 6 && c.IsDateFormat)
            .Select(c => ctx.Date(c))
            .OfType<DateOnly>()
            .Where(d => d.Month == month)
            .Distinct()
            .ToList();
        return dates.OrderBy(d => Math.Abs(d.Day - side.Day.In(d.Year, d.Month).Day)).Cast<DateOnly?>().FirstOrDefault();
    }

    private static void MapCash(MappingContext ctx, XlsxSheet sheet, List<Block> blocks)
    {
        var latest = blocks.Where(b => b.PayDate <= ctx.Today && b.LeftOver?.Number is not null).MaxBy(b => (b.PayDate, b.Side.LabelColumn));
        if (latest is null)
        {
            ctx.Output.Review("no-cash-balance", "No checking balance", "The budget has no past paycheck to take a left-over figure from. Enter your checking balance.");
            return;
        }

        var amount = ctx.Money(sheet, latest.LeftOver, "Current Left Over")!.Value;
        var source = ctx.Ref(sheet, latest.LeftOver!);
        ctx.CheckGoogleOnly(sheet, latest.LeftOver, "The left-over figure");
        ctx.Output.Balances.Add(new ProposedBalance(AccountKey, amount, latest.PayDate, $"Left over after the {ImportText.ShortDate(latest.PayDate)} paycheck", source));
        ctx.Output.Review("budget-cash", "Checking balance comes from the budget's left-over figure",
            $"The budget shows {ImportText.Amount(amount)} left after the {ImportText.Date(latest.PayDate)} paycheck and the lines marked paid. " +
            "That's the budget's arithmetic, not a bank balance. Enter today's checking balance so forecasts start from a real figure.",
            [source], AccountKey);
        if (latest.LeftOver!.Formula is { } formula && TypedNumber().IsMatch(formula))
        {
            ctx.Output.Note("typed-adjustment", "Left-over formulas include typed-in amounts",
                "At least one Current Left Over formula adds or subtracts a number typed into the formula. It's carried into the figure above as the sheet calculates it.",
                [source]);
        }
        else if (blocks.Any(b => b.LeftOver?.Formula is { } f && TypedNumber().IsMatch(f)))
        {
            var first = blocks.First(b => b.LeftOver?.Formula is { } f && TypedNumber().IsMatch(f));
            ctx.Output.Note("typed-adjustment", "Left-over formulas include typed-in amounts",
                "Some Current Left Over formulas add or subtract a number typed into the formula; the running left-over carries them forward.",
                [ctx.Ref(sheet, first.LeftOver!)]);
        }
    }

    private static void MapIncome(MappingContext ctx, XlsxSheet sheet, Side side, List<Block> blocks)
    {
        var paid = blocks.Where(b => b.NetPay is { IsPositive: true }).ToList();
        if (paid.Count == 0)
        {
            return;
        }

        var usual = paid.GroupBy(b => b.NetPay!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.First().PayDate).First().Key;
        var schedule = new Schedule(new MonthlyRule(side.Day), BusinessDayAdjustment.PreviousBusinessDay);
        var overrides = paid.Where(b => b.NetPay != usual).Select(b => new PayAmountOverride(Nominal(side, b.PayDate), b.NetPay!.Value)).ToList();
        ctx.Output.Income.Add(new ProposedIncome($"income:{side.Key}", $"Paycheck ({side.Name})", usual, schedule,
            [ctx.Ref(sheet, paid[0].Header), ctx.Ref(sheet, paid[^1].Header)], AccountKey, overrides.Count > 0 ? overrides : null));
        if (overrides.Count > 0)
        {
            ctx.Output.Note("pay-varies", $"Paycheck ({side.Name}) varies",
                $"Most checks are {ImportText.Amount(usual)}; {overrides.Count} differ and are kept as one-off amounts.", [ctx.Ref(sheet, paid[0].Header)]);
        }

        var mismatches = blocks
            .Where(b => b.HasHelperDate(ctx, sheet))
            .Select(b => (Block: b, Expected: schedule.Occurrences(b.PayDate.AddDays(-10), b.PayDate.AddDays(10), BankCalendar).Select(o => o.Date).FirstOrDefault()))
            .Where(x => x.Expected != default && x.Expected != x.Block.PayDate)
            .ToList();
        if (mismatches.Count > 0)
        {
            ctx.Output.Review("pay-date-mismatch", $"Paycheck ({side.Name}): some pay dates don't follow the usual rule",
                $"Mossmint expects the {side.Name} check on the {side.Day} or the business day before it. The budget has: " +
                string.Join("; ", mismatches.Select(m => $"{ImportText.ShortDate(m.Block.PayDate)} (rule: {ImportText.ShortDate(m.Expected)})")) + ".",
                [ctx.Ref(sheet, mismatches[0].Block.Header)], $"income:{side.Key}");
        }
    }

    // Lines in upcoming blocks become bills. A line that appears in a run of
    // consecutive checks is one monthly bill; gaps become skipped months, and
    // a line that stops before the sheet does gets an end date.
    private static void MapBills(MappingContext ctx, XlsxSheet sheet, Side side, List<Block> blocks, List<Block> allBlocks, IReadOnlyList<DebtFacts> debts)
    {
        var otherSideLabels = allBlocks.Where(b => b.Side.Key != side.Key && b.PayDate >= ctx.Today)
            .SelectMany(b => b.Lines).Select(l => Labels.Normalize(l.Label)).ToHashSet();
        var occurrences = blocks
            .SelectMany((block, index) => block.Lines
                .Where(l => !l.IsAdded && l.Amount is { IsPositive: true } && l.Label.Length > 0)
                .GroupBy(l => Labels.Normalize(l.Label))
                .Select(g => (Key: g.Key, Index: index, Block: block, Lines: g.ToList())))
            .GroupBy(o => o.Key);
        foreach (var group in occurrences)
        {
            var items = group.OrderBy(o => o.Index).ToList();
            foreach (var duplicate in items.Where(i => i.Lines.Count > 1))
            {
                ctx.Output.Note("duplicate-line", $"'{duplicate.Lines[0].Label}' appears {duplicate.Lines.Count} times on one check",
                    $"On the {ImportText.ShortDate(duplicate.Block.PayDate)} check the amounts are added together.",
                    [.. duplicate.Lines.Select(l => ctx.Ref(sheet, l.LabelCell!))]);
            }

            var amounts = items.Select(i => i.Lines.Aggregate(Money.Zero(ctx.Currency), (sum, l) => sum + l.Amount!.Value)).ToList();
            var first = items[0].Index;
            var last = items[^1].Index;
            var skips = Enumerable.Range(first, last - first + 1).Except(items.Select(i => i.Index))
                .Select(i => (ScheduleOverride)new SkipOccurrence(Nominal(side, blocks[i].PayDate))).ToList();
            var schedule = new Schedule(
                new MonthlyRule(side.Day),
                BusinessDayAdjustment.PreviousBusinessDay,
                first > 0 || items.Count == 1 ? Nominal(side, items[0].Block.PayDate) : null,
                last < blocks.Count - 1 || items.Count == 1 ? Nominal(side, items[^1].Block.PayDate) : null,
                skips.Count > 0 ? skips : null);

            var distinct = amounts.Distinct().ToList();
            var expected = amounts.GroupBy(a => a).OrderByDescending(g => g.Count()).ThenBy(g => amounts.IndexOf(g.Key)).First().Key;
            var amount = distinct.Count == 1
                ? AmountPolicy.Fixed(expected)
                : AmountPolicy.Estimated(expected, distinct.Min(), distinct.Max());

            var label = items[^1].Lines[0].Label;
            var name = otherSideLabels.Contains(group.Key) ? $"{label} ({side.Name})" : label;
            var debt = debts.FirstOrDefault(d => Labels.Normalize(d.Name) == group.Key);
            var sources = new[] { ctx.Ref(sheet, items[0].Lines[0].LabelCell!), ctx.Ref(sheet, items[^1].Lines[0].LabelCell!) }.Distinct().ToList();
            var key = $"bill:{side.Key}:{Labels.Slug(label)}";
            ctx.Output.Bills.Add(new ProposedBill(key, name, amount, schedule, debt is null ? BillPurpose.Expense : BillPurpose.DebtPayment,
                sources, debt?.AccountKey, AccountKey));

            if (debt?.RegularPayment is { } regular && distinct.Any(a => a != regular))
            {
                ctx.Output.Review("payment-differs", $"{debt.Name}: the budget pays a different amount",
                    $"The budget plans {string.Join(" / ", distinct.Select(ImportText.Amount))} toward {debt.Name}, but its workbook's regular payment is {ImportText.Amount(regular)}.",
                    [sources[0], debt.Source], debt.AccountKey);
            }
        }
    }

    private static void FlagUnpaidPastLines(MappingContext ctx, XlsxSheet sheet, List<Block> blocks)
    {
        foreach (var block in blocks.Where(b => b.PayDate < ctx.Today && b.PayDate >= ctx.Today.AddDays(-45)))
        {
            var unpaid = block.Lines.Where(l => l.Paid == false && l.Amount is { IsZero: false }).ToList();
            if (unpaid.Count > 0)
            {
                ctx.Output.Review("unpaid-lines", $"{unpaid.Count} line(s) on the {ImportText.ShortDate(block.PayDate)} check aren't marked paid",
                    $"{string.Join(", ", unpaid.Select(l => $"{l.Label} {ImportText.Amount(l.Amount!.Value)}"))}. If they're still due, add them; if they're paid, they need no action.",
                    [.. unpaid.Select(l => ctx.Ref(sheet, l.LabelCell ?? l.AmountCell!))]);
            }
        }
    }

    // Free text inside an upcoming block (a reminder typed next to the lines)
    // is something the user wanted to remember; it goes to review word for word.
    private static void FlagNotes(MappingContext ctx, XlsxSheet sheet, List<Block> blocks)
    {
        foreach (var block in blocks)
        {
            var flagColumn = block.Side.LabelColumn + 2;
            var lastRow = block.LeftOver?.Row ?? block.Total?.Row ?? block.Header.Row;
            var notes = sheet.Cells.Where(c => c.Row >= block.Header.Row && c.Row <= lastRow && c.Column == flagColumn && c.Kind == CellKind.Text && c.Value.Trim().Length > 0);
            foreach (var note in notes)
            {
                ctx.Output.Review("budget-note", $"Note on the {ImportText.ShortDate(block.PayDate)} check", note.Value.Trim().TrimStart('*').Trim(),
                    [ctx.Ref(sheet, note)]);
            }
        }
    }

    // Total Paid is recomputed from the lines its own formula covers. A
    // different saved value means the file wasn't recalculated before saving.
    private static void CheckTotals(MappingContext ctx, XlsxSheet sheet, List<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.Total is not { Formula: { } formula, Number: { } saved })
            {
                continue;
            }

            decimal? recomputed = null;
            if (SumIf().Match(formula) is { Success: true } sumIf)
            {
                var flags = Range(sumIf.Groups["flags"].Value);
                var values = Range(sumIf.Groups["values"].Value);
                var wanted = sumIf.Groups["when"].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                recomputed = flags.Zip(values).Where(p => sheet.Cell(p.First.Row, p.First.Column)?.Boolean == wanted)
                    .Sum(p => sheet.Cell(p.Second.Row, p.Second.Column)?.Number ?? 0m);
            }
            else if (Sum().Match(formula) is { Success: true } sum)
            {
                recomputed = Range(sum.Groups["range"].Value).Sum(p => sheet.Cell(p.Row, p.Column)?.Number ?? 0m);
            }

            if (recomputed is { } total && Math.Abs(total - saved) > 0.005m)
            {
                ctx.Output.Review("stale-cache", $"Total Paid on the {ImportText.ShortDate(block.PayDate)} check is out of date",
                    $"The saved total is {ImportText.Amount(new Money(saved, ctx.Currency))} but its lines add up to {ImportText.Amount(new Money(decimal.Round(total, 2), ctx.Currency))}. " +
                    "Open the workbook, let it recalculate, save it, and import it again.",
                    [ctx.Ref(sheet, block.Total)]);
            }
        }
    }

    private static void MapSavingsAndReserve(MappingContext ctx)
    {
        SavingsContributionTarget? target = null;
        CashReserveFloor? floor = null;
        var sources = new List<SourceRef>();

        var savings = HeaderTable.Find(ctx.Workbook.Sheet("Savings Tracker"), "Paycheck Date", "Savings Goal");
        if (savings is not null)
        {
            var rows = savings.Rows("Paycheck Date").Select(r => (Row: r, Date: ctx.Date(savings.Cell(r, "Paycheck Date")))).Where(r => r.Date is not null).ToList();
            var goals = rows.Where(r => r.Date >= ctx.Today)
                .Select(r => ctx.Money(savings.Sheet, savings.Cell(r.Row, "Savings Goal"), "Savings goal")).OfType<Money>().ToList();
            if (goals.Count > 0)
            {
                var goal = goals.GroupBy(g => g).OrderByDescending(g => g.Count()).First().Key;
                target = goal.IsPositive ? new SavingsContributionTarget(goal) : null;
                sources.Add(ctx.Ref(savings.Sheet, savings.Cell(rows.First(r => r.Date >= ctx.Today).Row, "Savings Goal")!));
                if (goals.Distinct().Count() > 1)
                {
                    ctx.Output.Note("savings-goal-varies", "The savings goal varies", $"Most checks aim for {ImportText.Amount(goal)}; that's the target Mossmint uses.", [sources[^1]]);
                }
            }

            var past = rows.LastOrDefault(r => r.Date <= ctx.Today);
            if (past.Row > 0 && savings.Cell(past.Row, "Running Savings") is { } running && ctx.Money(savings.Sheet, running, "Running savings") is { IsPositive: true } estimate)
            {
                var confirmed = savings.Column("Confirmed Saved") is { } c
                    ? rows.Select(r => ctx.Money(savings.Sheet, savings.Sheet.Cell(r.Row, c), "Confirmed saved")).OfType<Money>().Where(m => !m.IsZero).ToList()
                    : [];
                ctx.Output.Review("savings-estimate", "Savings balance isn't known",
                    $"The savings tracker estimates {ImportText.Amount(estimate)} saved by {ImportText.Date(past.Date!.Value)}, assuming each check's leftover up to the goal was set aside" +
                    (confirmed.Count > 0 ? $"; {confirmed.Count} amount(s) are marked confirmed" : "") +
                    ". Add your savings account with its real balance.",
                    [ctx.Ref(savings.Sheet, running)]);
            }
        }

        var dashboard = ctx.Workbook.Sheet("Debt Dashboard");
        var reserveLabel = dashboard?.Cells.FirstOrDefault(c => Labels.Normalize(c.Text) == "minimum reserve");
        if (dashboard is not null && reserveLabel is not null)
        {
            var value = dashboard.Cells.Where(c => c.Row == reserveLabel.Row && c.Column > reserveLabel.Column && c.Number is not null).MinBy(c => c.Column);
            if (ctx.Money(dashboard, value, "Minimum reserve") is { IsPositive: true } reserve)
            {
                floor = new CashReserveFloor(reserve);
                sources.Add(ctx.Ref(dashboard, value!));
            }

            ctx.Output.Note("dashboard-not-imported", "The Debt Dashboard's projections aren't imported",
                "Payoff dates and suggested extra payments there are calculated by Google Sheets from copies of the debt workbooks. Mossmint makes its own from the debt workbooks.",
                [ctx.Ref(dashboard, "A1")]);
        }

        if (target is not null || floor is not null)
        {
            ctx.Output.Settings = new ProposedSettings(sources, target, floor);
        }
    }

    private static void NoteGoogleOnly(MappingContext ctx)
    {
        foreach (var sheet in ctx.Workbook.Sheets.Where(s => s.Visibility != SheetVisibility.Visible))
        {
            var google = sheet.Cells.Count(c => c.IsGoogleOnly);
            ctx.Output.Note("hidden-sheet", $"Hidden sheet '{sheet.Name}' isn't imported",
                google > 0
                    ? $"It holds {google} cell(s) copied from another workbook by a Google Sheets formula. Mossmint reads the workbooks themselves instead."
                    : "Hidden sheets are kept out of the import.",
                [ctx.Ref(sheet, "A1")]);
        }
    }

    private static DateOnly Nominal(Side side, DateOnly payDate) => side.Day.In(payDate.Year, payDate.Month);

    private static int Month(string header) =>
        DateTime.ParseExact(HeaderPattern().Match(header).Groups["month"].Value, "MMMM", CultureInfo.InvariantCulture).Month;

    // A check labelled on the 28th or later is the month-end check.
    private static DayOfMonth DayOf(string header) =>
        int.Parse(HeaderPattern().Match(header).Groups["day"].Value, CultureInfo.InvariantCulture) is var day && day >= 28 ? DayOfMonth.Last : DayOfMonth.Of(day >= 13 && day <= 15 ? 15 : day);

    private static IEnumerable<(int Row, int Column)> Range(string reference)
    {
        var parts = reference.Replace("$", "", StringComparison.Ordinal).Split(':');
        var (r1, c1) = XlsxAddress.Parse(parts[0]);
        var (r2, c2) = parts.Length > 1 ? XlsxAddress.Parse(parts[1]) : (r1, c1);
        for (var r = r1; r <= r2; r++)
        {
            for (var c = c1; c <= c2; c++)
            {
                yield return (r, c);
            }
        }
    }

    [GeneratedRegex(@"^\s*(?<month>January|February|March|April|May|June|July|August|September|October|November|December)\s+(?<day>\d{1,2})(st|nd|rd|th)?\s+check\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^=\s*SUMIF\(\s*(?<flags>\$?[A-Z]+\$?\d+:\$?[A-Z]+\$?\d+)\s*,\s*(?<when>TRUE|FALSE)\s*,\s*(?<values>\$?[A-Z]+\$?\d+:\$?[A-Z]+\$?\d+)\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SumIf();

    [GeneratedRegex(@"^=\s*SUM\(\s*(?<range>\$?[A-Z]+\$?\d+(:\$?[A-Z]+\$?\d+)?)\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Sum();

    // A number with a decimal point inside a formula, e.g. "+26.85".
    [GeneratedRegex(@"[+\-]\s*\d+\.\d+")]
    private static partial Regex TypedNumber();

    private sealed record Side(string Key, string Name, int LabelColumn, DayOfMonth Day);

    private sealed record Line(string Label, Money? Amount, bool? Paid, bool IsAdded, string? Fill, XlsxCell? LabelCell, XlsxCell? AmountCell);

    private sealed record Block(Side Side, DateOnly PayDate, Money? NetPay, XlsxCell Header, List<Line> Lines, XlsxCell? Total, XlsxCell? LeftOver)
    {
        public bool HasHelperDate(MappingContext ctx, XlsxSheet sheet) =>
            LeftOver is not null && sheet.Cells.Any(c => c.Row == LeftOver.Row && c.Column > 6 && c.IsDateFormat && ctx.Date(c) == PayDate);
    }

    // "Blue = Money Spent" / "Purple = Money Added": the legend's own fill
    // colours say which lines were spent and which added money.
    private sealed class Legend
    {
        private readonly HashSet<string> _spent = [];
        private readonly HashSet<string> _added = [];

        public static Legend From(XlsxSheet sheet)
        {
            var legend = new Legend();
            foreach (var cell in sheet.Cells.Where(c => c.Kind == CellKind.Text && c.Fill is not null && c.Row <= 5))
            {
                var text = Labels.Normalize(cell.Value);
                if (text.EndsWith("money spent", StringComparison.Ordinal))
                {
                    legend._spent.Add(cell.Fill!);
                }
                else if (text.EndsWith("money added", StringComparison.Ordinal))
                {
                    legend._added.Add(cell.Fill!);
                }
            }

            return legend;
        }

        public bool IsSpent(string fill) => _spent.Contains(fill);

        public bool IsAdded(string fill) => _added.Contains(fill);
    }
}
