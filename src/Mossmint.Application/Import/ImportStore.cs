using Mossmint.Application.Data;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;

namespace Mossmint.Application.Import;

public enum ReviewStatus
{
    Open,
    Resolved,
    Dismissed,
}

// Something an import couldn't settle on its own. It stays open until the user
// confirms, corrects or dismisses it; nothing is corrected silently.
public sealed record ReviewItem(
    Guid Id,
    Guid RunId,
    string Code,
    FindingSeverity Severity,
    string Title,
    string Detail,
    IReadOnlyList<string> Sources,
    string? SubjectKey,
    string Fingerprint,
    ReviewStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt = null,
    string? Resolution = null);

public enum LinkedEntity
{
    Account,
    Debt,
    Bill,
    Income,
    Settings,
}

// Which profile record a source key became, the source file or connector it
// belongs to, and a fingerprint of what the source said last time. Comparing
// the record's current fingerprint with this one shows whether the user has
// edited it since, so a re-import never overwrites their changes.
public sealed record ImportLink(string Key, LinkedEntity Entity, Guid EntityId, string Scope, string Fingerprint, Guid RunId);

// A transaction that came from a source, keyed by the source's own ID.
public sealed record ImportedTransaction(Transaction Transaction, string Key, bool RemovedAtSource = false);

public sealed record ImportRun(
    Guid Id,
    string Mapper,
    ImportOrigin Origin,
    DateTimeOffset At,
    IReadOnlyList<SourceFileInfo> Files,
    string Report);

// Everything one import writes. The store applies it in a single transaction
// after taking an encrypted snapshot, so it lands whole or not at all.
public sealed record ImportCommit(
    ImportRun Run,
    Guid? BasedOnRun,
    IReadOnlyList<Account> NewAccounts,
    IReadOnlyList<BalanceSnapshot> Balances,
    IReadOnlyList<DebtAccount> Debts,
    IReadOnlyList<RecurringBill> Bills,
    IReadOnlyList<IncomeSchedule> Income,
    PlanSettings? Settings,
    IReadOnlyList<ImportedTransaction> Transactions,
    IReadOnlyList<ReviewItem> ReviewItems,
    IReadOnlyList<RawRecord> RawRecords,
    IReadOnlyList<ImportLink> Links);

public sealed class StaleImportPlanException()
    : InvalidOperationException("The profile changed after this import was previewed. Preview it again.");

// Import bookkeeping inside the encrypted profile.
public interface IImportStore
{
    IReadOnlyList<ImportLink> Links();

    IReadOnlyList<ImportedTransaction> Transactions();

    IReadOnlyList<ReviewItem> ReviewItems();

    // Oldest first.
    IReadOnlyList<ImportRun> Runs();

    IReadOnlyList<RawRecord> RawRecords(Guid runId);

    // Throws StaleImportPlanException if another import landed after BasedOnRun.
    void Commit(ImportCommit commit);

    void CloseReviewItem(Guid id, ReviewStatus status, string? resolution);

    void ReopenReviewItem(Guid id);
}
