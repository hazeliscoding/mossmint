using System.Text.Json.Serialization;

namespace Mossmint.Domain.Scheduling;

// A day within a month: a number (clamped to the month's length, so "the 31st"
// is the 30th in April and the 28th or 29th in February) or the last day.
public readonly record struct DayOfMonth
{
    [JsonConstructor]
    public DayOfMonth(int day, bool isLastDay)
    {
        if (isLastDay ? day != 0 : day is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, "Use a day from 1 to 31, or the last day with day 0.");
        }

        Day = day;
        IsLastDay = isLastDay;
    }

    public int Day { get; }

    public bool IsLastDay { get; }

    public static DayOfMonth Last { get; } = new(0, isLastDay: true);

    public static DayOfMonth Of(int day) => new(day, isLastDay: false);

    public DateOnly In(int year, int month)
    {
        var length = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, IsLastDay ? length : Math.Min(Day, length));
    }

    public override string ToString() => IsLastDay ? "last day" : $"day {Day}";
}
