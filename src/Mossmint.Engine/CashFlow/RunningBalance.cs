using Mossmint.Domain;
using Mossmint.Domain.Accounts;

namespace Mossmint.Engine.CashFlow;

// Cash in an account now: its latest balance plus the entries the user recorded
// after it (docs/financial-models/cash-flow.md, "Opening balance"). An entry dated
// before the balance is already in it, and so is one recorded earlier the same
// day. Imported entries are evidence the imported balance already reflects.
public static class RunningBalance
{
    public static OpeningBalance From(BalanceSnapshot balance, IEnumerable<Transaction> entries) =>
        new(entries.Where(e => After(balance, e)).Aggregate(balance.Balance, (sum, e) => sum + e.Amount), balance.Provenance);

    public static bool After(BalanceSnapshot balance, Transaction entry) =>
        entry.Account == balance.Account
        && entry.Status != TransactionStatus.Void
        && entry.Provenance.Source == SourceKind.UserConfirmed
        && (entry.EffectiveDate > balance.Provenance.AsOf
            || (entry.EffectiveDate == balance.Provenance.AsOf && entry.RecordedAt > balance.RecordedAt));
}
