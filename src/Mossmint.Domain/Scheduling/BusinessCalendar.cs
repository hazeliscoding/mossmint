namespace Mossmint.Domain.Scheduling;

public interface IBusinessCalendar
{
    bool IsBusinessDay(DateOnly date);
}

public enum CalendarKind
{
    // Saturdays and Sundays only. Nothing is assumed about holidays.
    WeekendsOnly,

    // Weekends plus the US Federal Reserve holiday schedule, which governs when
    // ACH direct deposits settle. It is an assumption about the user's bank.
    UsFederalReserve,
}

public enum BusinessDayAdjustment
{
    None,
    PreviousBusinessDay,
    NextBusinessDay,
}

// The calendar as data, so engine inputs stay serializable and replayable.
public sealed record CalendarSpec(CalendarKind Kind, IReadOnlyList<DateOnly>? ExtraClosedDays = null)
{
    public static CalendarSpec WeekendsOnly { get; } = new(CalendarKind.WeekendsOnly);

    public static CalendarSpec UsFederalReserve { get; } = new(CalendarKind.UsFederalReserve);

    public IBusinessCalendar Build() => new Calendar(this);

    private sealed class Calendar(CalendarSpec spec) : IBusinessCalendar
    {
        private readonly HashSet<DateOnly> _extraClosed = [.. spec.ExtraClosedDays ?? []];

        public bool IsBusinessDay(DateOnly date) =>
            date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && !_extraClosed.Contains(date)
            && !(spec.Kind == CalendarKind.UsFederalReserve && UsFederalReserveHolidays.IsHoliday(date));
    }
}

public static class BusinessDays
{
    // Weekends plus a run of holidays never exceed a couple of weeks; a longer
    // walk means the calendar has no business days, which is a configuration bug.
    private const int MaxSteps = 31;

    public static DateOnly Adjust(DateOnly date, BusinessDayAdjustment adjustment, IBusinessCalendar calendar)
    {
        var step = adjustment switch
        {
            BusinessDayAdjustment.None => 0,
            BusinessDayAdjustment.PreviousBusinessDay => -1,
            BusinessDayAdjustment.NextBusinessDay => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(adjustment), adjustment, null),
        };

        if (step == 0)
        {
            return date;
        }

        var result = date;
        for (var i = 0; !calendar.IsBusinessDay(result); i++)
        {
            if (i == MaxSteps)
            {
                throw new InvalidOperationException($"No business day within {MaxSteps} days of {date}.");
            }

            result = result.AddDays(step);
        }

        return result;
    }
}

// Federal Reserve Bank holidays. When a holiday falls on a Sunday the Fed closes
// the following Monday; when it falls on a Saturday the Fed stays open on Friday.
public static class UsFederalReserveHolidays
{
    public static bool IsHoliday(DateOnly date) => For(date.Year).Contains(date);

    public static IReadOnlyList<DateOnly> For(int year)
    {
        var days = new List<DateOnly>
        {
            Observed(new DateOnly(year, 1, 1)),
            NthWeekday(year, 1, DayOfWeek.Monday, 3), // Martin Luther King Jr. Day
            NthWeekday(year, 2, DayOfWeek.Monday, 3), // Washington's Birthday
            LastWeekday(year, 5, DayOfWeek.Monday), // Memorial Day
            Observed(new DateOnly(year, 7, 4)),
            NthWeekday(year, 9, DayOfWeek.Monday, 1), // Labor Day
            NthWeekday(year, 10, DayOfWeek.Monday, 2), // Columbus Day
            Observed(new DateOnly(year, 11, 11)),
            NthWeekday(year, 11, DayOfWeek.Thursday, 4), // Thanksgiving
            Observed(new DateOnly(year, 12, 25)),
        };

        if (year >= 2022)
        {
            days.Add(Observed(new DateOnly(year, 6, 19))); // Juneteenth
        }

        return [.. days.Where(d => d.DayOfWeek != DayOfWeek.Saturday).Order()];
    }

    private static DateOnly Observed(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? date.AddDays(1) : date;

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (7 * (n - 1)));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
        return last.AddDays(-offset);
    }
}
