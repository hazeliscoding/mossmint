namespace Mossmint.Domain.Accounts;

public readonly record struct TransactionId(Guid Value);

public readonly record struct TransferGroupId(Guid Value);

public enum TransactionStatus
{
    Pending,
    Posted,
    Void,
}

// Amount is signed the way the account reports it: deposits and card charges
// are positive; withdrawals and card payments are negative.
public sealed record Transaction(
    TransactionId Id,
    AccountId Account,
    DateOnly EffectiveDate,
    Money Amount,
    TransactionStatus Status,
    string Description,
    Provenance Provenance,
    DateOnly? PostedDate = null,
    string? Category = null,
    TransferGroupId? TransferGroup = null);

public static class Transfers
{
    // The change in net worth when an amount is booked on an account. A card
    // payment lowers checking and lowers the amount owed: net worth is unchanged.
    public static Money NetWorthEffect(Account account, Money amount) => account.IsLiability ? -amount : amount;

    // Both sides of a transfer (including a card or loan payment) must move
    // together and cancel out, so a payment is never also counted as spending.
    public static IReadOnlyList<string> Problems(IReadOnlyList<(Account Account, Transaction Leg)> legs)
    {
        var problems = new List<string>();
        if (legs.Count < 2)
        {
            problems.Add("A transfer needs at least two legs.");
            return problems;
        }

        if (legs.Select(l => l.Leg.TransferGroup).Distinct().Count() != 1 || legs[0].Leg.TransferGroup is null)
        {
            problems.Add("Every leg must belong to the same transfer group.");
        }

        if (legs.Any(l => l.Leg.Account != l.Account.Id))
        {
            problems.Add("A leg is booked on a different account than the one given.");
        }

        if (legs.Any(l => l.Leg.Status == TransactionStatus.Void))
        {
            problems.Add("A void leg can't be part of an active transfer.");
        }

        if (legs.Select(l => l.Leg.Amount.Currency).Distinct().Count() != 1)
        {
            problems.Add("All legs must use the same currency.");
            return problems;
        }

        var net = legs.Aggregate(Money.Zero(legs[0].Leg.Amount.Currency), (sum, l) => sum + NetWorthEffect(l.Account, l.Leg.Amount));
        if (!net.IsZero)
        {
            problems.Add($"The legs don't cancel out (net {net}).");
        }

        return problems;
    }
}
