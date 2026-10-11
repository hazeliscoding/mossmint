using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Engine.Cards;
using Mossmint.Engine.CashFlow;
using Mossmint.Engine.Loans;
using Mossmint.Engine.Payoff;

namespace Mossmint.Application.Forecasting;

public enum PayoffOrder
{
    TodaysPlan,
    Avalanche,
    Snowball,
    Custom,
}

// Safe: daily cash stays at or above the cushion with the option's extra.
public enum ExtraStatus
{
    Safe,
    BelowCushion,
    NeedsConfirmation,
}

public sealed record PayoffStep(AccountId Account, string Name, DateOnly? PaidOff);

// One column of the comparison. Steps are the debts in the order they're paid
// off; Problem says why there's no debt-free date. Lowest is the lowest daily
// cash through the horizon with this option's extra. When stress spending and
// late paychecks would leave room only for a smaller extra, ExtraIfTight is that
// extra and DebtFreeIfTight the debt-free date it gives (null if none).
public sealed record PayoffOption(
    PayoffOrder Order,
    Money Extra,
    IReadOnlyList<PayoffStep> Steps,
    DateOnly? DebtFree,
    Money? Interest,
    string? Problem,
    DateOnly LowestDate,
    Money Lowest,
    ExtraStatus Status,
    Confidence Confidence,
    bool IsApproximate,
    Money? ExtraIfTight = null,
    DateOnly? DebtFreeIfTight = null);

// LargestSafeExtra is the most that can go to debt each month: Safe with the
// amount, Shortfall when cash already falls below the cushion with no extra, or
// NeedsConfirmation with what to confirm first.
public sealed record PayoffComparison(SafeToSpendView LargestSafeExtra, Money Cushion, DateOnly Through, IReadOnlyList<PayoffOption> Options) : ForecastOutcome;

// Today's plan against avalanche, snowball and a custom order, all paying the
// same extra each month (docs/financial-models/payoff.md). Nothing is saved.
public static class Scenarios
{
    public static ForecastOutcome Compare(IProfileData data, DateOnly today, Money extra, IReadOnlyList<AccountId>? customOrder = null, ForecastAssumptions? assumptions = null)
    {
        if (data.Debts().Count == 0)
        {
            return new ForecastUnavailable(["Add a debt to compare ways of paying it off."]);
        }

        var book = DebtBook.Load(data);
        var (known, problems, baseline) = book;
        var through = book.Horizon(today);
        var (cash, unavailable) = Forecasts.CashPlan(data, today, through, assumptions);
        if (cash is null)
        {
            return unavailable!;
        }

        var plan = cash.Plan;
        var cushion = plan.ReserveFloor?.Minimum ?? Money.Zero(extra.Currency);
        var debts = book.Debts(plan);
        var extras = SafeExtra.Monthly(plan, extra);
        var largest = Forecasts.View(SafeExtra.Largest(plan), through);

        // The extra that would still fit if things get tight; unknown until the inputs are confirmed.
        var harshest = Forecasts.With(plan, ForecastAssumptions.Harshest);
        Money? tight = SafeExtra.Largest(harshest) switch
        {
            SafeAmount s => s.Amount,
            Shortfall => Money.Zero(extra.Currency),
            _ => null,
        };
        var without = CashFlowSimulator.Run(plan);
        var withExtra = CashFlowSimulator.Run(plan with { ExtraDebtPayments = extras });
        var avalanche = book.Order(PayoffOrder.Avalanche, debts, null);
        var snowball = book.Order(PayoffOrder.Snowball, debts, null);
        var custom = book.Order(PayoffOrder.Custom, debts, customOrder);

        return new PayoffComparison(largest, cushion, through,
        [
            Option(PayoffOrder.TodaysPlan, baseline, book.Order(PayoffOrder.TodaysPlan, debts, null), Money.Zero(extra.Currency), without),
            Option(PayoffOrder.Avalanche, PayoffPlanner.Plan(debts, avalanche, extras), avalanche, extra, withExtra),
            Option(PayoffOrder.Snowball, PayoffPlanner.Plan(debts, snowball, extras), snowball, extra, withExtra),
            Option(PayoffOrder.Custom, PayoffPlanner.Plan(debts, custom, extras), custom, extra, withExtra),
        ]);

        PayoffOption Option(PayoffOrder order, PayoffPlan payoff, IReadOnlyList<AccountId> sequence, Money amount, CashFlowForecast forecast)
        {
            var ifTight = tight is { } fits && amount > fits
                ? PayoffPlanner.Plan(debts, sequence, SafeExtra.Monthly(harshest, fits))
                : null;
            var paidOff = payoff.Debts.ToDictionary(d => d.Account, d => d.Outcome is PaysOff p ? p.PayoffDate : (DateOnly?)null);
            var names = known.ToDictionary(k => k.Account, k => k.Name);
            IEnumerable<AccountId> steps = order == PayoffOrder.TodaysPlan
                ? sequence.OrderBy(id => paidOff[id] ?? DateOnly.MaxValue)
                : sequence;
            var stuck = payoff.Debts.FirstOrDefault(d => d.Outcome is not PaysOff);
            var problem = problems.Count > 0 ? string.Join(" ", problems)
                : stuck?.Outcome switch
                {
                    NeverPaysOff n => $"{names[stuck.Account]}: {n.Reason}",
                    InsufficientData i => $"{names[stuck.Account]}: {string.Join(" ", i.Missing)}",
                    _ => null,
                };
            var status = largest.State == SafeToSpendState.NeedsConfirmation ? ExtraStatus.NeedsConfirmation
                : forecast.Lowest.Amount >= cushion ? ExtraStatus.Safe
                : ExtraStatus.BelowCushion;
            return new PayoffOption(
                order,
                amount,
                [.. steps.Select(id => new PayoffStep(id, names[id], paidOff[id]))],
                problems.Count > 0 ? null : payoff.DebtFree,
                problems.Count > 0 ? null : payoff.TotalInterest,
                problem,
                forecast.Lowest.Date,
                forecast.Lowest.Amount,
                status,
                payoff.Debts.Select(d => d.Confidence).DefaultIfEmpty(Confidence.NeedsReview).Min(),
                known.Any(k => k.Input is CreditCardInput),
                ifTight is null ? null : tight,
                problems.Count > 0 ? null : ifTight?.DebtFree);
        }
    }
}
