using Mossmint.Application.Forecasting;
using Mossmint.Application.Setup;

namespace Mossmint.Application.Tests;

// The made-up household behind "Try a sample household" and the screenshots.
public class SampleHouseholdTests
{
    [Fact]
    public void The_sample_is_a_complete_household_dated_around_today()
    {
        var data = new InMemoryProfileData();
        var today = new DateOnly(2027, 3, 14);

        SampleHousehold.Fill(data, new FixedClock(new DateTimeOffset(today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)));

        Forecasts.Build(data, today).Should().BeOfType<ProfileForecast>("every page has something to show");
        (data.Income().Count, data.Debts().Count, data.Goals().Count).Should().Be((1, 2, 3));
        data.Accounts().SelectMany(a => data.Balances(a.Id)).Should().AllSatisfy(b =>
            b.Provenance.AsOf.Should().BeOnOrBefore(today).And.BeOnOrAfter(today.AddDays(-30)));
        data.Goals().Select(g => g.TargetDate).OfType<DateOnly>().Should().AllSatisfy(d => d.Should().BeAfter(today));
    }
}
