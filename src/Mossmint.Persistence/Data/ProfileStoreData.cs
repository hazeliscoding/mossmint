using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mossmint.Application.Data;
using Mossmint.Domain;
using Mossmint.Domain.Accounts;
using Mossmint.Domain.Debts;
using Mossmint.Domain.Planning;
using Mossmint.Domain.Scheduling;
using Mossmint.Engine.CashFlow;

namespace Mossmint.Persistence.Data;

// IProfileData over the encrypted database. Each write is one SaveChanges, so
// a record and its audit entry land together or not at all.
internal sealed class ProfileStoreData(ProfileDbContext db, TimeProvider clock) : IProfileData
{
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
        db.Accounts.Add(new AccountRow
        {
            Id = account.Id.Value,
            Name = account.Name,
            Kind = account.Kind.ToString(),
            Currency = account.Currency.Code,
            CreatedAt = clock.GetUtcNow(),
        });
        Audit("account.added", "Account", account.Id.Value, account.Name);
        if (opening is not null)
        {
            AddSnapshot(opening);
        }

        db.SaveChanges();
    }

    public void RecordBalance(BalanceSnapshot snapshot)
    {
        AddSnapshot(snapshot);
        db.SaveChanges();
    }

    public IReadOnlyList<RecurringBill> Bills() =>
        [.. db.Bills.AsNoTracking().OrderBy(b => b.Name).AsEnumerable().Select(ToBill)];

    public void SaveBill(RecurringBill bill)
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
        db.SaveChanges();
    }

    public IReadOnlyList<IncomeSchedule> Income() =>
        [.. db.Income.AsNoTracking().OrderBy(i => i.Name).AsEnumerable().Select(ToIncome)];

    public void SaveIncome(IncomeSchedule income)
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
        db.SaveChanges();
    }

    public IReadOnlyList<DebtAccount> Debts() =>
        [.. db.Debts.AsNoTracking().AsEnumerable().Select(ToDebt).OrderBy(d => d.Account.Value)];

    public void SaveDebt(DebtAccount debt)
    {
        var row = db.Debts.Find(debt.Account.Value);
        var isNew = row is null;
        row ??= db.Debts.Add(new DebtRow { AccountId = debt.Account.Value }).Entity;
        row.TermsJson = JsonSerializer.Serialize(debt.Terms, Json);
        row.OriginalPrincipalMinor = debt.OriginalPrincipal?.ToMinorUnits();
        row.OpenedOn = debt.OpenedOn;
        row.UpdatedAt = clock.GetUtcNow();
        Audit(isNew ? "debt.added" : "debt.changed", "Debt", debt.Account.Value, debt.Terms.GetType().Name);
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
        var row = db.Settings.Find(1) ?? db.Settings.Add(new SettingsRow { Id = 1 }).Entity;
        row.Currency = settings.Currency.Code;
        row.SavingsTargetMinor = settings.SavingsTarget?.PerPaycheck.ToMinorUnits();
        row.ReserveFloorMinor = settings.ReserveFloor?.Minimum.ToMinorUnits();
        row.AllowancesJson = settings.Allowances is { Count: > 0 } a ? JsonSerializer.Serialize(a, Json) : null;
        row.UpdatedAt = clock.GetUtcNow();
        Audit("settings.changed", "PlanSettings", Guid.Empty, "Plan settings");
        db.SaveChanges();
    }

    public ProfileCounts Counts() => new(
        db.Accounts.Count(), db.Balances.Count(), db.Bills.Count(), db.Income.Count(), db.Debts.Count(), db.Audit.Count());

    private void AddSnapshot(BalanceSnapshot snapshot)
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

    // Audit summaries name what changed, never amounts, so nothing sensitive
    // depends on the audit trail staying inside the database.
    private void Audit(string action, string entity, Guid id, string summary) =>
        db.Audit.Add(new AuditRow { At = clock.GetUtcNow(), Action = action, Entity = entity, EntityId = id.ToString("N"), Summary = summary });

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
