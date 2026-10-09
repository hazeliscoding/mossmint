using System.Text.Json.Serialization;

namespace Mossmint.Domain.Debts;

public enum DayCountConvention
{
    // Annual rate / 365 every day, leap years included. The common US consumer
    // loan convention, and what the reference spreadsheets use.
    Actual365Fixed,

    // Annual rate / 360 every day.
    Actual360,

    // Annual rate / 365, or / 366 for days in a leap year.
    ActualActual,
}

public static class DayCount
{
    public static decimal DailyRate(AnnualRate rate, DayCountConvention convention, DateOnly day) => convention switch
    {
        DayCountConvention.Actual365Fixed => rate.Value / 365m,
        DayCountConvention.Actual360 => rate.Value / 360m,
        DayCountConvention.ActualActual => rate.Value / (DateTime.IsLeapYear(day.Year) ? 366m : 365m),
        _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, null),
    };
}

public enum RateKind
{
    Fixed,
    Variable,
    Promotional,
}

// A rate in force from EffectiveFrom until the next segment. A future variable
// rate nobody has announced must be marked IsAssumption.
public sealed record RateSegment(DateOnly EffectiveFrom, AnnualRate Rate, RateKind Kind = RateKind.Fixed, bool IsAssumption = false);

public sealed record RateSchedule
{
    [JsonConstructor]
    public RateSchedule(IReadOnlyList<RateSegment> segments)
    {
        if (segments.Count == 0)
        {
            throw new ArgumentException("A rate schedule needs at least one segment.", nameof(segments));
        }

        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].EffectiveFrom <= segments[i - 1].EffectiveFrom)
            {
                throw new ArgumentException("Rate segments must be in strictly increasing date order.", nameof(segments));
            }
        }

        Segments = segments;
    }

    public IReadOnlyList<RateSegment> Segments { get; }

    [JsonIgnore]
    public bool HasAssumptions => Segments.Any(s => s.IsAssumption);

    public static RateSchedule Fixed(AnnualRate rate, DateOnly from) => new([new RateSegment(from, rate)]);

    // Null before the first segment: the rate for that period is unknown.
    public RateSegment? SegmentOn(DateOnly day) => Segments.LastOrDefault(s => s.EffectiveFrom <= day);

    public IEnumerable<DateOnly> ChangesAfter(DateOnly day) => Segments.Select(s => s.EffectiveFrom).Where(d => d > day);
}

public enum AllocationTarget
{
    Fees,
    Interest,
    Principal,
}

// The order a payment pays down what is owed, as the contract states it.
public sealed record PaymentAllocation
{
    [JsonConstructor]
    public PaymentAllocation(IReadOnlyList<AllocationTarget> order)
    {
        if (order.Count != 3 || order.Distinct().Count() != 3)
        {
            throw new ArgumentException("List fees, interest and principal once each.", nameof(order));
        }

        Order = order;
    }

    public IReadOnlyList<AllocationTarget> Order { get; }

    public static PaymentAllocation Standard { get; } =
        new([AllocationTarget.Fees, AllocationTarget.Interest, AllocationTarget.Principal]);
}

// A fee charged on principal prepaid before a date, when the contract has one.
public sealed record PrepaymentPenalty(decimal FractionOfPrepaidPrincipal, DateOnly AppliesUntil);
