using System.Text.Json.Serialization;

namespace Mossmint.Domain.Scheduling;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$rule")]
[JsonDerivedType(typeof(WeeklyRule), "weekly")]
[JsonDerivedType(typeof(SemiMonthlyRule), "semi-monthly")]
[JsonDerivedType(typeof(MonthlyRule), "monthly")]
public abstract record ScheduleRule
{
    // Unadjusted dates within [from, to], ascending.
    public abstract IEnumerable<DateOnly> NominalDates(DateOnly from, DateOnly to);

    protected static IEnumerable<(int Year, int Month)> Months(DateOnly from, DateOnly to)
    {
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            yield return (month.Year, month.Month);
        }
    }
}

// Every IntervalWeeks weeks (1 = weekly, 2 = every two weeks), in step with Anchor.
public sealed record WeeklyRule : ScheduleRule
{
    public WeeklyRule(DateOnly anchor, int intervalWeeks = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalWeeks, 1);
        Anchor = anchor;
        IntervalWeeks = intervalWeeks;
    }

    public DateOnly Anchor { get; }

    public int IntervalWeeks { get; }

    public override IEnumerable<DateOnly> NominalDates(DateOnly from, DateOnly to)
    {
        var step = 7 * IntervalWeeks;
        var offset = from.DayNumber - Anchor.DayNumber;
        var periods = (int)Math.Ceiling(offset / (double)step);
        for (var date = Anchor.AddDays(periods * step); date <= to; date = date.AddDays(step))
        {
            yield return date;
        }
    }
}

// Twice a month, e.g. the 15th and the last day.
public sealed record SemiMonthlyRule(DayOfMonth First, DayOfMonth Second) : ScheduleRule
{
    public override IEnumerable<DateOnly> NominalDates(DateOnly from, DateOnly to) =>
        Months(from, to)
            .SelectMany(m => new[] { First.In(m.Year, m.Month), Second.In(m.Year, m.Month) })
            .Distinct()
            .Order()
            .Where(d => d >= from && d <= to);
}

public sealed record MonthlyRule(DayOfMonth Day) : ScheduleRule
{
    public override IEnumerable<DateOnly> NominalDates(DateOnly from, DateOnly to) =>
        Months(from, to).Select(m => Day.In(m.Year, m.Month)).Where(d => d >= from && d <= to);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$override")]
[JsonDerivedType(typeof(SkipOccurrence), "skip")]
[JsonDerivedType(typeof(MoveOccurrence), "move")]
[JsonDerivedType(typeof(AddOccurrence), "add")]
public abstract record ScheduleOverride;

public sealed record SkipOccurrence(DateOnly Nominal) : ScheduleOverride;

public sealed record MoveOccurrence(DateOnly Nominal, DateOnly To) : ScheduleOverride;

public sealed record AddOccurrence(DateOnly Date) : ScheduleOverride;

public enum OccurrenceKind
{
    Regular,
    AdjustedForBusinessDay,
    Moved,
    Added,
}

// Nominal is the date the rule produced; Date is when it actually happens.
public sealed record Occurrence(DateOnly Nominal, DateOnly Date, OccurrenceKind Kind);

public sealed record Schedule(
    ScheduleRule Rule,
    BusinessDayAdjustment Adjustment = BusinessDayAdjustment.None,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    IReadOnlyList<ScheduleOverride>? Overrides = null)
{
    // Adjustment can move a date across the window edge; generating a margin on
    // both sides and filtering on the final date keeps edge dates correct.
    private const int AdjustmentMarginDays = 14;

    public IReadOnlyList<Occurrence> Occurrences(DateOnly from, DateOnly to, IBusinessCalendar calendar)
    {
        if (to < from)
        {
            return [];
        }

        var overrides = Overrides ?? [];
        var skipped = overrides.OfType<SkipOccurrence>().Select(o => o.Nominal).ToHashSet();
        var moved = overrides.OfType<MoveOccurrence>().ToDictionary(o => o.Nominal, o => o.To);

        var generated = Rule.NominalDates(from.AddDays(-AdjustmentMarginDays), to.AddDays(AdjustmentMarginDays))
            .Where(InEffect)
            .Where(nominal => !skipped.Contains(nominal))
            .Select(nominal => moved.TryGetValue(nominal, out var target)
                ? new Occurrence(nominal, target, OccurrenceKind.Moved)
                : Adjusted(nominal, calendar));

        var added = overrides.OfType<AddOccurrence>().Select(o => new Occurrence(o.Date, o.Date, OccurrenceKind.Added));

        return [.. generated.Concat(added)
            .Where(o => o.Date >= from && o.Date <= to)
            .OrderBy(o => o.Date)
            .ThenBy(o => o.Nominal)];
    }

    private bool InEffect(DateOnly nominal) =>
        (StartsOn is null || nominal >= StartsOn) && (EndsOn is null || nominal <= EndsOn);

    private Occurrence Adjusted(DateOnly nominal, IBusinessCalendar calendar)
    {
        var date = BusinessDays.Adjust(nominal, Adjustment, calendar);
        return new Occurrence(nominal, date, date == nominal ? OccurrenceKind.Regular : OccurrenceKind.AdjustedForBusinessDay);
    }
}
