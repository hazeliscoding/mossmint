using Mossmint.Application.Forecasting;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Application.Data;

// Plan-wide settings that aren't tied to one account. DebtPlan is the payoff plan
// applied from Scenarios; without one, each debt is paid on its own.
public sealed record PlanSettings(
    Currency Currency,
    SavingsContributionTarget? SavingsTarget = null,
    CashReserveFloor? ReserveFloor = null,
    IReadOnlyList<SpendingAllowance>? Allowances = null,
    DebtPlan? DebtPlan = null)
{
    public static PlanSettings Empty(Currency currency) => new(currency);
}

// An extra each month from the first paycheck, paid to the debts in Order's way
// (docs/financial-models/payoff.md). Custom is the user's order for a custom plan.
public sealed record DebtPlan(Money MonthlyExtra, PayoffOrder Order, IReadOnlyList<AccountId> Custom);

// Row counts per record type: a restored backup must match its source exactly.
// Later counts default to zero so backups written before they existed still verify.
public sealed record ProfileCounts(
    int Accounts,
    int BalanceSnapshots,
    int Bills,
    int Income,
    int Debts,
    int AuditEvents,
    int Transactions = 0,
    int ReviewItems = 0,
    int ImportRuns = 0,
    int Goals = 0);

// The profile's stored records. Every change is recorded in the audit trail.
public interface IProfileData
{
    IReadOnlyList<Account> Accounts();

    // Newest first.
    IReadOnlyList<BalanceSnapshot> Balances(AccountId account);

    void AddAccount(Account account, BalanceSnapshot? opening = null);

    void RecordBalance(BalanceSnapshot snapshot);

    IReadOnlyList<RecurringBill> Bills();

    void SaveBill(RecurringBill bill);

    // Deletes a bill outright; callers keep paid bills and end them instead.
    void RemoveBill(BillId bill);

    IReadOnlyList<IncomeSchedule> Income();

    void SaveIncome(IncomeSchedule income);

    IReadOnlyList<DebtAccount> Debts();

    void SaveDebt(DebtAccount debt);

    // Newest first, void entries included so corrections stay visible.
    IReadOnlyList<Transaction> Transactions(AccountId account);

    void RecordTransaction(Transaction transaction);

    // Undo keeps the entry, marked void, rather than deleting it.
    void VoidTransaction(TransactionId id);

    IReadOnlyList<SavingsGoal> Goals();

    void SaveGoal(SavingsGoal goal);

    PlanSettings Settings();

    void SaveSettings(PlanSettings settings);

    ProfileCounts Counts();
}
