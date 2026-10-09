using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Mossmint.Domain.Scheduling;

namespace Mossmint.Domain.Tests;

public class ScheduleTests
{
    private static readonly IBusinessCalendar Weekends = CalendarSpec.WeekendsOnly.Build();
    private static readonly IBusinessCalendar Fed = CalendarSpec.UsFederalReserve.Build();

    private static readonly Schedule FifteenthAndLastDay = new(
        new SemiMonthlyRule(DayOfMonth.Of(15), DayOfMonth.Last),
        BusinessDayAdjustment.PreviousBusinessDay);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static IEnumerable<DateOnly> Dates(Schedule s, DateOnly from, DateOnly to, IBusinessCalendar? calendar = null) =>
        s.Occurrences(from, to, calendar ?? Weekends).Select(o => o.Date);

    [Fact]
    public void Fifteenth_and_last_day_move_back_to_the_previous_business_day()
    {
        // 2026: Aug 15 is a Saturday, Oct 31 a Saturday, Nov 15 a Sunday.
        Dates(FifteenthAndLastDay, D(2026, 8, 1), D(2026, 11, 30)).Should().Equal(
            D(2026, 8, 14), D(2026, 8, 31),
            D(2026, 9, 15), D(2026, 9, 30),
            D(2026, 10, 15), D(2026, 10, 30),
            D(2026, 11, 13), D(2026, 11, 30));
    }

    [Fact]
    public void Last_day_follows_the_length_of_february()
    {
        var lastDay = new Schedule(new MonthlyRule(DayOfMonth.Last));

        Dates(lastDay, D(2027, 2, 1), D(2027, 2, 28)).Should().Equal(D(2027, 2, 28));
        Dates(lastDay, D(2028, 2, 1), D(2028, 2, 29)).Should().Equal(D(2028, 2, 29));
    }

    [Fact]
    public void The_thirty_first_clamps_to_short_months()
    {
        var thirtyFirst = new Schedule(new MonthlyRule(DayOfMonth.Of(31)));

        Dates(thirtyFirst, D(2027, 1, 1), D(2027, 4, 30)).Should().Equal(
            D(2027, 1, 31), D(2027, 2, 28), D(2027, 3, 31), D(2027, 4, 30));
    }

    [Fact]
    public void Last_business_day_is_last_day_adjusted_backwards()
    {
        var lastBusinessDay = new Schedule(new MonthlyRule(DayOfMonth.Last), BusinessDayAdjustment.PreviousBusinessDay);

        // Jan 31 2027 is a Sunday; Jul 31 2027 a Saturday.
        Dates(lastBusinessDay, D(2027, 1, 1), D(2027, 1, 31)).Should().Equal(D(2027, 1, 29));
        Dates(lastBusinessDay, D(2027, 7, 1), D(2027, 7, 31)).Should().Equal(D(2027, 7, 30));
    }

    [Fact]
    public void An_adjusted_date_from_outside_the_window_is_included()
    {
        var first = new Schedule(new MonthlyRule(DayOfMonth.Of(1)), BusinessDayAdjustment.PreviousBusinessDay);

        // Nov 1 2026 is a Sunday, so it is paid on Friday Oct 30.
        var occurrences = first.Occurrences(D(2026, 10, 2), D(2026, 10, 31), Weekends);

        occurrences.Should().ContainSingle()
            .Which.Should().Be(new Occurrence(D(2026, 11, 1), D(2026, 10, 30), OccurrenceKind.AdjustedForBusinessDay));
    }

    [Fact]
    public void Biweekly_stays_in_step_with_its_anchor_in_both_directions()
    {
        var biweekly = new Schedule(new WeeklyRule(D(2026, 10, 2), intervalWeeks: 2));

        Dates(biweekly, D(2026, 9, 1), D(2026, 10, 31)).Should().Equal(
            D(2026, 9, 4), D(2026, 9, 18), D(2026, 10, 2), D(2026, 10, 16), D(2026, 10, 30));
    }

    [Fact]
    public void Overrides_skip_move_and_add_dates()
    {
        var schedule = FifteenthAndLastDay with
        {
            Overrides =
            [
                new SkipOccurrence(D(2026, 9, 15)),
                new MoveOccurrence(D(2026, 9, 30), D(2026, 10, 2)),
                new AddOccurrence(D(2026, 9, 22)),
            ],
        };

        schedule.Occurrences(D(2026, 9, 1), D(2026, 10, 5), Weekends).Should().Equal(
            new Occurrence(D(2026, 9, 22), D(2026, 9, 22), OccurrenceKind.Added),
            new Occurrence(D(2026, 9, 30), D(2026, 10, 2), OccurrenceKind.Moved));
    }

    [Fact]
    public void Start_and_end_bound_the_nominal_dates()
    {
        var bounded = new Schedule(new MonthlyRule(DayOfMonth.Of(15)), StartsOn: D(2026, 9, 1), EndsOn: D(2026, 11, 15));

        Dates(bounded, D(2026, 1, 1), D(2026, 12, 31)).Should().Equal(D(2026, 9, 15), D(2026, 10, 15), D(2026, 11, 15));
    }

    [Fact]
    public void Federal_reserve_holidays_for_2026_match_the_published_schedule()
    {
        // July 4 falls on a Saturday in 2026, so the Fed stays open on July 3.
        UsFederalReserveHolidays.For(2026).Should().Equal(
            D(2026, 1, 1), D(2026, 1, 19), D(2026, 2, 16), D(2026, 5, 25), D(2026, 6, 19),
            D(2026, 9, 7), D(2026, 10, 12), D(2026, 11, 11), D(2026, 11, 26), D(2026, 12, 25));
    }

    [Fact]
    public void A_sunday_holiday_is_observed_on_monday()
    {
        // July 4 2027 is a Sunday.
        UsFederalReserveHolidays.IsHoliday(D(2027, 7, 5)).Should().BeTrue();
        UsFederalReserveHolidays.IsHoliday(D(2027, 7, 4)).Should().BeFalse();
    }

    [Fact]
    public void Holiday_calendars_push_paydays_further()
    {
        var mondays = new Schedule(new WeeklyRule(D(2026, 1, 5)), BusinessDayAdjustment.NextBusinessDay);

        // Labor Day, Monday Sep 7 2026: weekends-only keeps Monday, the Fed calendar moves to Tuesday.
        Dates(mondays, D(2026, 9, 7), D(2026, 9, 8), Weekends).Should().Equal(D(2026, 9, 7));
        Dates(mondays, D(2026, 9, 7), D(2026, 9, 8), Fed).Should().Equal(D(2026, 9, 8));
    }

    [Fact]
    public void Extra_closed_days_are_honoured()
    {
        var calendar = new CalendarSpec(CalendarKind.WeekendsOnly, [D(2026, 10, 15)]).Build();

        Dates(FifteenthAndLastDay, D(2026, 10, 1), D(2026, 10, 20), calendar).Should().Equal(D(2026, 10, 14));
    }

    [Fact]
    public void A_schedule_round_trips_through_json()
    {
        var schedule = FifteenthAndLastDay with { Overrides = [new SkipOccurrence(D(2026, 9, 15))] };

        var json = JsonSerializer.Serialize(schedule);
        var copy = JsonSerializer.Deserialize<Schedule>(json)!;

        copy.Occurrences(D(2026, 8, 1), D(2026, 12, 31), Weekends)
            .Should().Equal(schedule.Occurrences(D(2026, 8, 1), D(2026, 12, 31), Weekends));
        json.Should().Contain("\"$rule\":\"semi-monthly\"").And.Contain("\"$override\":\"skip\"");
    }

    private static readonly Arbitrary<DateOnly> Months2020s =
        Gen.Choose(0, 12 * 10).Select(i => new DateOnly(2020, 1, 1).AddMonths(i)).ToArbitrary();

    [Fact]
    public void Semi_monthly_gives_two_dates_a_month_and_they_are_business_days() =>
        Prop.ForAll(Months2020s, monthStart =>
        {
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var raw = new Schedule(FifteenthAndLastDay.Rule).Occurrences(monthStart, monthEnd, Weekends);
            var adjusted = FifteenthAndLastDay.Occurrences(monthStart.AddDays(-15), monthEnd.AddDays(15), Fed);

            return raw.Count == 2
                && adjusted.All(o => Fed.IsBusinessDay(o.Date) && o.Date <= o.Nominal)
                && adjusted.Select(o => o.Date).SequenceEqual(adjusted.Select(o => o.Date).Order());
        }).QuickCheckThrowOnFailure();
}
