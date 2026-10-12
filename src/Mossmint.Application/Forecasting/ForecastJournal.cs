using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Application.Forecasting;

public sealed record JournalDay(DateOnly Date, Money Low, Money Closing);

// One forecast as it was made, for 0.9's backtests: the daily balances it
// expected for its account, its lowest point and its safe-to-spend answer,
// with the balance it started from. Comparing it with what later happened
// gives the error by days ahead and in the lowest point before payday.
public sealed record JournalEntry(
    DateOnly MadeOn,
    AccountId Account,
    Money Opening,
    DateOnly OpeningAsOf,
    Confidence OpeningConfidence,
    IReadOnlyList<JournalDay> Days,
    DateOnly LowestDate,
    Money Lowest,
    SafeToSpendState SafeState,
    Money? SafeAmount,
    DateOnly SafeThrough);

public static class ForecastJournal
{
    // Keeps today's forecast. One entry is kept per day: a later forecast the
    // same day replaces it, so the day's entry is what was known at its end.
    public static void Record(IProfileData data, ForecastOutcome outcome, DateOnly today)
    {
        if (outcome is ProfileForecast f)
        {
            data.RecordForecast(new JournalEntry(today, f.Account, f.Opening, f.OpeningProvenance.AsOf, f.OpeningProvenance.Confidence,
                [.. f.Days.Select(d => new JournalDay(d.Date, d.Low, d.Closing))], f.LowestDate, f.Lowest,
                f.SafeToSpend.State, f.SafeToSpend.Amount, f.SafeToSpend.Through));
        }
    }
}
