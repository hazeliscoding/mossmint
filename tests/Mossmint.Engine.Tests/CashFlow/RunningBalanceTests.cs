using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Engine.Tests.CashFlow;

public class RunningBalanceTests
{
    private static readonly AccountId Checking = new(Guid.NewGuid());
    private static readonly DateOnly AsOf = new(2026, 10, 8);
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly BalanceSnapshot Balance = new(Checking, Money.Usd(1_240m), Provenance.UserConfirmed(AsOf), RecordedAt: ConfirmedAt);

    [Fact]
    public void Entries_recorded_after_the_balance_move_it()
    {
        // 1,240 − 70 (dated the next day) − 115 (same day, typed in after the balance) + 40.
        var opening = RunningBalance.From(Balance, [
            Entry(-70m, AsOf.AddDays(1), ConfirmedAt.AddDays(1)),
            Entry(-115m, AsOf, ConfirmedAt.AddHours(3)),
            Entry(40m, AsOf.AddDays(2), ConfirmedAt.AddDays(2)),
        ]);

        opening.Amount.Should().Be(Money.Usd(1_095m));
        opening.Provenance.Should().Be(Balance.Provenance);
    }

    [Fact]
    public void Entries_the_balance_already_includes_are_left_out()
    {
        var opening = RunningBalance.From(Balance, [
            Entry(-50m, AsOf.AddDays(-1), ConfirmedAt.AddHours(1)),
            Entry(-60m, AsOf, ConfirmedAt.AddHours(-1)),
            Entry(-70m, AsOf.AddDays(1), ConfirmedAt.AddDays(1)) with { Status = TransactionStatus.Void },
            Entry(-80m, AsOf.AddDays(1), ConfirmedAt.AddDays(1)) with { Provenance = Provenance.Imported(AsOf.AddDays(1)) },
            Entry(-90m, AsOf.AddDays(1), ConfirmedAt.AddDays(1)) with { Account = new AccountId(Guid.NewGuid()) },
        ]);

        opening.Amount.Should().Be(Money.Usd(1_240m));
    }

    private static Transaction Entry(decimal amount, DateOnly date, DateTimeOffset recordedAt) => new(
        new TransactionId(Guid.NewGuid()), Checking, date, Money.Usd(amount), TransactionStatus.Posted, "Entry",
        Provenance.UserConfirmed(date), RecordedAt: recordedAt);
}
