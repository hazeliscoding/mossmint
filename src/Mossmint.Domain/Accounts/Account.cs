using System.Text.Json.Serialization;

namespace Mossmint.Domain.Accounts;

// Identifiers are created by the application layer (the domain has no clock or
// randomness) and never derived from names, merchants or amounts.
public readonly record struct AccountId(Guid Value);

public enum AccountKind
{
    Checking,
    Savings,
    Cash,
    CreditCard,
    InstallmentLoan,
    TrackingOnly,
}

public sealed record Account(AccountId Id, string Name, AccountKind Kind, Currency Currency)
{
    [JsonIgnore]
    public bool IsLiability => Kind is AccountKind.CreditCard or AccountKind.InstallmentLoan;
}

public enum ReconciliationState
{
    Unreconciled,
    Reconciled,
    Discrepancy,
}

// What an account held on a date, as the institution reports it: liabilities
// are the amount owed, as a positive number. A snapshot is evidence, never a
// transaction, and a newer snapshot never rewrites the ledger. Loans that report
// unpaid interest separately put it in AccruedInterest; Balance is then principal.
// RecordedAt is when it was entered, which orders it against same-day entries.
public sealed record BalanceSnapshot(
    AccountId Account,
    Money Balance,
    Provenance Provenance,
    ReconciliationState State = ReconciliationState.Unreconciled,
    Money? AccruedInterest = null,
    DateTimeOffset? RecordedAt = null);
