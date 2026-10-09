using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mossmint.Application.Data;
using Mossmint.Application.Import;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Persistence.Data;

// IProfileData and IImportStore over the encrypted database. Each write is one
// SaveChanges, so a record and its audit entry land together or not at all; an
// import is one transaction after a snapshot.
internal sealed class ProfileStoreData(ProfileDbContext db, TimeProvider clock, Func<string, string>? snapshot = null) : IProfileData, IImportStore
{
    // Pre-import snapshots kept per profile; older ones are deleted after a successful import.
    public const int ImportSnapshotsKept = 5;

    private static readonly JsonSerializerOptions Json = new();

    public IReadOnlyList<Account> Accounts() =>
        [.. db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt).AsEnumerable().Select(ToAccount)];

    public IReadOnlyList<BalanceSnapshot> Balances(AccountId account) =>
        [.. db.Balances.AsNoTracking()
            .Where(b => b.AccountId == account.Value)
            .OrderByDescending(b => b.AsOf).ThenByDescending(b => b.Id)
            .AsEnumerable().Select(ToSnapshot)];

    public void AddAccount(Account account, BalanceSnapshot? opening = null)
    {
        StageAccount(account);
        if (opening is not null)
        {
            StageSnapshot(opening);
        }

        db.SaveChanges();
    }

    public void RecordBalance(BalanceSnapshot snapshot)
    {
        StageSnapshot(snapshot);
        db.SaveChanges();
    }

    public IReadOnlyList<RecurringBill> Bills() =>
        [.. db.Bills.AsNoTracking().OrderBy(b => b.Name).AsEnumerable().Select(ToBill)];

    public void SaveBill(RecurringBill bill)
    {
        StageBill(bill);
        db.SaveChanges();
    }

    public IReadOnlyList<IncomeSchedule> Income() =>
        [.. db.Income.AsNoTracking().OrderBy(i => i.Name).AsEnumerable().Select(ToIncome)];

    public void SaveIncome(IncomeSchedule income)
    {
        StageIncome(income);
        db.SaveChanges();
    }

    public IReadOnlyList<DebtAccount> Debts() =>
        [.. db.Debts.AsNoTracking().AsEnumerable().Select(ToDebt).OrderBy(d => d.Account.Value)];

    public void SaveDebt(DebtAccount debt)
    {
        StageDebt(debt);
        db.SaveChanges();
    }

    public PlanSettings Settings()
    {
        var row = db.Settings.AsNoTracking().SingleOrDefault();
        if (row is null)
        {
            return PlanSettings.Empty(Currency.Usd);
        }

        var currency = Currency.Of(row.Currency);
        return new PlanSettings(
            currency,
            row.SavingsTargetMinor is { } target ? new SavingsContributionTarget(Money.FromMinorUnits(target, currency)) : null,
            row.ReserveFloorMinor is { } floor ? new CashReserveFloor(Money.FromMinorUnits(floor, currency)) : null,
            row.AllowancesJson is { } json ? JsonSerializer.Deserialize<List<SpendingAllowance>>(json, Json) : null);
    }

    public void SaveSettings(PlanSettings settings)
    {
        StageSettings(settings);
        db.SaveChanges();
    }

    public ProfileCounts Counts() => new(
        db.Accounts.Count(), db.Balances.Count(), db.Bills.Count(), db.Income.Count(), db.Debts.Count(), db.Audit.Count(),
        db.Transactions.Count(), db.ReviewItems.Count(), db.ImportRuns.Count());

    public IReadOnlyList<ImportLink> Links() =>
        [.. db.ImportLinks.AsNoTracking().AsEnumerable().Select(l =>
            new ImportLink(l.Key, Enum.Parse<LinkedEntity>(l.Entity), l.EntityId, l.Scope, l.Fingerprint, l.RunId))];

    public IReadOnlyList<ImportedTransaction> Transactions() =>
        [.. db.Transactions.AsNoTracking().Where(t => t.SourceKey != null).OrderBy(t => t.EffectiveDate).AsEnumerable()
            .Select(t => new ImportedTransaction(ToTransaction(t), t.SourceKey!, t.RemovedAtSource))];

    public IReadOnlyList<ReviewItem> ReviewItems() =>
        [.. db.ReviewItems.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).AsEnumerable().Select(ToReviewItem)];

    public IReadOnlyList<ImportRun> Runs() =>
        [.. db.ImportRuns.AsNoTracking().OrderBy(r => r.At).ThenBy(r => r.Id).AsEnumerable().Select(r => new ImportRun(
            r.Id, r.Mapper, Enum.Parse<ImportOrigin>(r.Origin), r.At,
            JsonSerializer.Deserialize<List<SourceFileInfo>>(r.FilesJson, Json)!, r.Report))];

    public IReadOnlyList<RawRecord> RawRecords(Guid runId) =>
        [.. db.RawRecords.AsNoTracking().Where(r => r.RunId == runId).OrderBy(r => r.Id).AsEnumerable().Select(r =>
            new RawRecord(r.Kind, new SourceRef(r.File, r.Location), JsonSerializer.Deserialize<Dictionary<string, string>>(r.FieldsJson, Json)!))];

    public void Commit(ImportCommit commit)
    {
        var lastRun = db.ImportRuns.AsNoTracking().OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Select(r => (Guid?)r.Id).FirstOrDefault();
        if (lastRun != commit.BasedOnRun)
        {
            throw new StaleImportPlanException();
        }

        _ = (snapshot ?? throw new InvalidOperationException("Imports need an unlocked profile session."))("pre-import");
        db.ChangeTracker.Clear();
        using var transaction = db.Database.BeginTransaction();
        try
        {
            var run = commit.Run;
            db.ImportRuns.Add(new ImportRunRow
            {
                Id = run.Id,
                Mapper = run.Mapper,
                Origin = run.Origin.ToString(),
                At = run.At,
                FilesJson = JsonSerializer.Serialize(run.Files, Json),
                Report = run.Report,
            });
            db.SaveChanges();

            foreach (var account in commit.NewAccounts)
            {
                StageAccount(account);
            }

            db.SaveChanges();
            foreach (var debt in commit.Debts)
            {
                StageDebt(debt);
            }

            foreach (var bill in commit.Bills)
            {
                StageBill(bill);
            }

            foreach (var income in commit.Income)
            {
                StageIncome(income);
            }

            if (commit.Settings is { } settings)
            {
                StageSettings(settings);
            }

            foreach (var balance in commit.Balances)
            {
                StageSnapshot(balance);
            }

            foreach (var imported in commit.Transactions)
            {
                StageTransaction(imported);
            }

            foreach (var item in commit.ReviewItems)
            {
                db.ReviewItems.Add(ToRow(item));
            }

            foreach (var raw in commit.RawRecords)
            {
                db.RawRecords.Add(new RawRecordRow
                {
                    RunId = run.Id,
                    Kind = raw.Kind,
                    File = raw.Source.File,
                    Location = raw.Source.Location,
                    FieldsJson = JsonSerializer.Serialize(raw.Fields, Json),
                });
            }

            foreach (var link in commit.Links)
            {
                var row = db.ImportLinks.Find(link.Entity.ToString(), link.Key)
                    ?? db.ImportLinks.Add(new ImportLinkRow { Entity = link.Entity.ToString(), Key = link.Key }).Entity;
                row.EntityId = link.EntityId;
                row.Scope = link.Scope;
                row.Fingerprint = link.Fingerprint;
                row.RunId = link.RunId;
            }

            Audit("import.committed", "ImportRun", run.Id, $"{run.Mapper}: {run.Files.Count} source(s), {commit.ReviewItems.Count} review item(s)");
            db.SaveChanges();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public void CloseReviewItem(Guid id, ReviewStatus status, string? resolution)
    {
        if (status == ReviewStatus.Open)
        {
            throw new ArgumentException("Use ReopenReviewItem to reopen an item.", nameof(status));
        }

        var row = db.ReviewItems.Find(id) ?? throw new KeyNotFoundException("No such review item.");
        row.Status = status.ToString();
        row.ClosedAt = clock.GetUtcNow();
        row.Resolution = resolution;
        Audit(status == ReviewStatus.Resolved ? "review.resolved" : "review.dismissed", "ReviewItem", id, row.Code);
        db.SaveChanges();
    }

    public void ReopenReviewItem(Guid id)
    {
        var row = db.ReviewItems.Find(id) ?? throw new KeyNotFoundException("No such review item.");
        row.Status = nameof(ReviewStatus.Open);
        row.ClosedAt = null;
        row.Resolution = null;
        Audit("review.reopened", "ReviewItem", id, row.Code);
        db.SaveChanges();
    }

    private void StageAccount(Account account)
    {
        db.Accounts.Add(new AccountRow
        {
            Id = account.Id.Value,
            Name = account.Name,
            Kind = account.Kind.ToString(),
            Currency = account.Currency.Code,
            CreatedAt = clock.GetUtcNow(),
        });
        Audit("account.added", "Account", account.Id.Value, account.Name);
    }

    private void StageBill(RecurringBill bill)
    {
        var row = db.Bills.Find(bill.Id.Value);
        var isNew = row is null;
        row ??= db.Bills.Add(new BillRow { Id = bill.Id.Value }).Entity;
        row.Name = bill.Name;
        row.Purpose = bill.Purpose.ToString();
        row.AmountJson = JsonSerializer.Serialize(bill.Amount, Json);
        row.ScheduleJson = JsonSerializer.Serialize(bill.DueDates, Json);
        row.FundingAccountId = bill.FundingAccount?.Value;
        row.TargetAccountId = bill.TargetAccount?.Value;
        row.IsAutopay = bill.IsAutopay;
        row.ProvenanceJson = JsonSerializer.Serialize(bill.Provenance, Json);
        row.UpdatedAt = clock.GetUtcNow();
        Audit(isNew ? "bill.added" : "bill.changed", "Bill", bill.Id.Value, bill.Name);
    }

    private void StageIncome(IncomeSchedule income)
    {
        var row = db.Income.Find(income.Id.Value);
        var isNew = row is null;
        row ??= db.Income.Add(new IncomeRow { Id = income.Id.Value }).Entity;
        row.Name = income.Name;
        row.NetPayMinor = income.NetPay.ToMinorUnits();
        row.Currency = income.NetPay.Currency.Code;
        row.ScheduleJson = JsonSerializer.Serialize(income.PayDates, Json);
        row.OverridesJson = income.AmountOverrides is { Count: > 0 } o ? JsonSerializer.Serialize(o, Json) : null;
        row.DepositAccountId = income.DepositAccount?.Value;
        row.ProvenanceJson = JsonSerializer.Serialize(income.Provenance, Json);
        row.UpdatedAt = clock.GetUtcNow();
        Audit(isNew ? "income.added" : "income.changed", "Income", income.Id.Value, income.Name);
    }

    private void StageDebt(DebtAccount debt)
    {
        var row = db.Debts.Find(debt.Account.Value);
        var isNew = row is null;
        row ??= db.Debts.Add(new DebtRow { AccountId = debt.Account.Value }).Entity;
        row.TermsJson = JsonSerializer.Serialize(debt.Terms, Json);
        row.OriginalPrincipalMinor = debt.OriginalPrincipal?.ToMinorUnits();
        row.OpenedOn = debt.OpenedOn;
        row.UpdatedAt = clock.GetUtcNow();
        Audit(isNew ? "debt.added" : "debt.changed", "Debt", debt.Account.Value, debt.Terms.GetType().Name);
    }

    private void StageSettings(PlanSettings settings)
    {
        var row = db.Settings.Find(1) ?? db.Settings.Add(new SettingsRow { Id = 1 }).Entity;
        row.Currency = settings.Currency.Code;
        row.SavingsTargetMinor = settings.SavingsTarget?.PerPaycheck.ToMinorUnits();
        row.ReserveFloorMinor = settings.ReserveFloor?.Minimum.ToMinorUnits();
        row.AllowancesJson = settings.Allowances is { Count: > 0 } a ? JsonSerializer.Serialize(a, Json) : null;
        row.UpdatedAt = clock.GetUtcNow();
        Audit("settings.changed", "PlanSettings", Guid.Empty, "Plan settings");
    }

    private void StageSnapshot(BalanceSnapshot snapshot)
    {
        db.Balances.Add(new BalanceRow
        {
            AccountId = snapshot.Account.Value,
            AsOf = snapshot.Provenance.AsOf,
            AmountMinor = snapshot.Balance.ToMinorUnits(),
            AccruedInterestMinor = snapshot.AccruedInterest?.ToMinorUnits(),
            Currency = snapshot.Balance.Currency.Code,
            ProvenanceJson = JsonSerializer.Serialize(snapshot.Provenance, Json),
            State = snapshot.State.ToString(),
            RecordedAt = clock.GetUtcNow(),
        });
        Audit("balance.recorded", "Account", snapshot.Account.Value, snapshot.Provenance.Source.ToString());
    }

    private void StageTransaction(ImportedTransaction imported)
    {
        var t = imported.Transaction;
        var row = db.Transactions.Find(t.Id.Value);
        var isNew = row is null;
        row ??= db.Transactions.Add(new TransactionRow { Id = t.Id.Value }).Entity;
        row.AccountId = t.Account.Value;
        row.EffectiveDate = t.EffectiveDate;
        row.PostedDate = t.PostedDate;
        row.AmountMinor = t.Amount.ToMinorUnits();
        row.Currency = t.Amount.Currency.Code;
        row.Status = t.Status.ToString();
        row.Description = t.Description;
        row.Category = t.Category;
        row.TransferGroupId = t.TransferGroup?.Value;
        row.ProvenanceJson = JsonSerializer.Serialize(t.Provenance, Json);
        row.SourceKey = imported.Key;
        row.RemovedAtSource = imported.RemovedAtSource;
        row.UpdatedAt = clock.GetUtcNow();
        Audit(isNew ? "transaction.added" : imported.RemovedAtSource ? "transaction.removed-at-source" : "transaction.changed", "Transaction", t.Id.Value, "Imported transaction");
    }

    // Audit summaries name what changed, never amounts, so nothing sensitive
    // depends on the audit trail staying inside the database.
    private void Audit(string action, string entity, Guid id, string summary) =>
        db.Audit.Add(new AuditRow { At = clock.GetUtcNow(), Action = action, Entity = entity, EntityId = id.ToString("N"), Summary = summary });

    private static ReviewItemRow ToRow(ReviewItem item) => new()
    {
        Id = item.Id,
        RunId = item.RunId,
        Code = item.Code,
        Severity = item.Severity.ToString(),
        Title = item.Title,
        Detail = item.Detail,
        SourcesJson = JsonSerializer.Serialize(item.Sources, Json),
        SubjectKey = item.SubjectKey,
        Fingerprint = item.Fingerprint,
        Status = item.Status.ToString(),
        CreatedAt = item.CreatedAt,
        ClosedAt = item.ClosedAt,
        Resolution = item.Resolution,
    };

    private static ReviewItem ToReviewItem(ReviewItemRow row) => new(
        row.Id, row.RunId, row.Code, Enum.Parse<FindingSeverity>(row.Severity), row.Title, row.Detail,
        JsonSerializer.Deserialize<List<string>>(row.SourcesJson, Json)!, row.SubjectKey, row.Fingerprint,
        Enum.Parse<ReviewStatus>(row.Status), row.CreatedAt, row.ClosedAt, row.Resolution);

    private static Account ToAccount(AccountRow row) =>
        new(new AccountId(row.Id), row.Name, Enum.Parse<AccountKind>(row.Kind), Currency.Of(row.Currency));

    private static BalanceSnapshot ToSnapshot(BalanceRow row)
    {
        var currency = Currency.Of(row.Currency);
        return new BalanceSnapshot(
            new AccountId(row.AccountId),
            Money.FromMinorUnits(row.AmountMinor, currency),
            JsonSerializer.Deserialize<Provenance>(row.ProvenanceJson, Json)!,
            Enum.Parse<ReconciliationState>(row.State),
            row.AccruedInterestMinor is { } accrued ? Money.FromMinorUnits(accrued, currency) : null);
    }

    private static Transaction ToTransaction(TransactionRow row) => new(
        new TransactionId(row.Id),
        new AccountId(row.AccountId),
        row.EffectiveDate,
        Money.FromMinorUnits(row.AmountMinor, Currency.Of(row.Currency)),
        Enum.Parse<TransactionStatus>(row.Status),
        row.Description,
        JsonSerializer.Deserialize<Provenance>(row.ProvenanceJson, Json)!,
        row.PostedDate,
        row.Category,
        row.TransferGroupId is { } group ? new TransferGroupId(group) : null);

    private static RecurringBill ToBill(BillRow row) => new(
        new BillId(row.Id),
        row.Name,
        JsonSerializer.Deserialize<AmountPolicy>(row.AmountJson, Json)!,
        JsonSerializer.Deserialize<Schedule>(row.ScheduleJson, Json)!,
        Enum.Parse<BillPurpose>(row.Purpose),
        JsonSerializer.Deserialize<Provenance>(row.ProvenanceJson, Json)!,
        row.FundingAccountId is { } funding ? new AccountId(funding) : null,
        row.TargetAccountId is { } target ? new AccountId(target) : null,
        row.IsAutopay);

    private static IncomeSchedule ToIncome(IncomeRow row) => new(
        new IncomeId(row.Id),
        row.Name,
        Money.FromMinorUnits(row.NetPayMinor, Currency.Of(row.Currency)),
        JsonSerializer.Deserialize<Schedule>(row.ScheduleJson, Json)!,
        JsonSerializer.Deserialize<Provenance>(row.ProvenanceJson, Json)!,
        row.OverridesJson is { } json ? JsonSerializer.Deserialize<List<PayAmountOverride>>(json, Json) : null,
        row.DepositAccountId is { } deposit ? new AccountId(deposit) : null);

    private DebtAccount ToDebt(DebtRow row)
    {
        var terms = JsonSerializer.Deserialize<DebtTerms>(row.TermsJson, Json)!;
        var currency = Currency.Of(db.Accounts.AsNoTracking().Single(a => a.Id == row.AccountId).Currency);
        return new DebtAccount(
            new AccountId(row.AccountId),
            terms,
            row.OriginalPrincipalMinor is { } original ? Money.FromMinorUnits(original, currency) : null,
            row.OpenedOn);
    }
}
