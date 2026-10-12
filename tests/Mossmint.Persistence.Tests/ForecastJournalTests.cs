using Mossmint.Application.Forecasting;
using Mossmint.Application.Profiles;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Persistence.Tests;

public sealed class ForecastJournalTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private readonly TempRoot _root = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero));
    private readonly IProfileSession _session;
    private readonly AccountId _checking = new(Guid.CreateVersion7());

    public ForecastJournalTests() =>
        _session = _root.Store(_clock).Create("Juniper", "moss under the old oak", unlockOnThisDevice: false).Session;

    public void Dispose()
    {
        _session.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void One_entry_is_kept_per_day_and_a_past_day_is_never_rewritten()
    {
        _session.Data.RecordForecast(Entry(Today.AddDays(-1), 500m));
        _session.Data.RecordForecast(Entry(Today, 400m));
        _session.Data.RecordForecast(Entry(Today, 380m));
        _session.Data.RecordForecast(Entry(Today.AddDays(-1), 999m));

        var journal = _session.Data.ForecastJournal();

        journal.Select(e => (e.MadeOn, e.Lowest)).Should().Equal((Today.AddDays(-1), Money.Usd(500m)), (Today, Money.Usd(380m)));
        journal[1].Should().BeEquivalentTo(Entry(Today, 380m), "every figure comes back exactly");
    }

    private JournalEntry Entry(DateOnly madeOn, decimal lowest) => new(
        madeOn, _checking, Money.Usd(1_240.50m), madeOn.AddDays(-1), Confidence.Confirmed,
        [new JournalDay(madeOn, Money.Usd(lowest), Money.Usd(1_240.50m)), new JournalDay(madeOn.AddDays(1), Money.Usd(lowest), Money.Usd(lowest + 10m))],
        madeOn.AddDays(1), Money.Usd(lowest), SafeToSpendState.Safe, Money.Usd(120m), madeOn.AddDays(3));
}
