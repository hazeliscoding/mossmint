using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mossmint.Persistence.Data;

// Storage rows. They are not domain types: RowMapping converts in both
// directions, so the domain never depends on how a profile is stored. Money is
// whole minor units plus an ISO currency code; nested terms and schedules are
// the same JSON the engine's replay tests round-trip.
internal sealed class AccountRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class BalanceRow
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public DateOnly AsOf { get; set; }
    public long AmountMinor { get; set; }
    public long? AccruedInterestMinor { get; set; }
    public string Currency { get; set; } = "";
    public string ProvenanceJson { get; set; } = "";
    public string State { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

internal sealed class BillRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string AmountJson { get; set; } = "";
    public string ScheduleJson { get; set; } = "";
    public Guid? FundingAccountId { get; set; }
    public Guid? TargetAccountId { get; set; }
    public bool IsAutopay { get; set; }
    public string ProvenanceJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class IncomeRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public long NetPayMinor { get; set; }
    public string Currency { get; set; } = "";
    public string ScheduleJson { get; set; } = "";
    public string? OverridesJson { get; set; }
    public Guid? DepositAccountId { get; set; }
    public string ProvenanceJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DebtRow
{
    public Guid AccountId { get; set; }
    public string TermsJson { get; set; } = "";
    public long? OriginalPrincipalMinor { get; set; }
    public DateOnly? OpenedOn { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SettingsRow
{
    public int Id { get; set; }
    public string Currency { get; set; } = "";
    public long? SavingsTargetMinor { get; set; }
    public long? ReserveFloorMinor { get; set; }
    public string? AllowancesJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TransactionRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly? PostedDate { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Category { get; set; }
    public Guid? TransferGroupId { get; set; }
    public string ProvenanceJson { get; set; } = "";

    // The source's own key ("<account key>|<external id>") for imported rows.
    public string? SourceKey { get; set; }
    public bool RemovedAtSource { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ImportRunRow
{
    public Guid Id { get; set; }
    public string Mapper { get; set; } = "";
    public string Origin { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string FilesJson { get; set; } = "";
    public string Report { get; set; } = "";
}

internal sealed class ImportLinkRow
{
    public string Entity { get; set; } = "";
    public string Key { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Scope { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid RunId { get; set; }
}

internal sealed class ReviewItemRow
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string SourcesJson { get; set; } = "";
    public string? SubjectKey { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? Resolution { get; set; }
}

// A source row as read, before mapping: the trail from a figure to its cell.
internal sealed class RawRecordRow
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string Kind { get; set; } = "";
    public string File { get; set; } = "";
    public string Location { get; set; } = "";
    public string FieldsJson { get; set; } = "";
}

// What changed and when. Lives inside the encrypted profile, never in logs.
internal sealed class AuditRow
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Summary { get; set; } = "";
}

internal sealed class ProfileDbContext(DbContextOptions<ProfileDbContext> options) : DbContext(options)
{
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<BalanceRow> Balances => Set<BalanceRow>();
    public DbSet<BillRow> Bills => Set<BillRow>();
    public DbSet<IncomeRow> Income => Set<IncomeRow>();
    public DbSet<DebtRow> Debts => Set<DebtRow>();
    public DbSet<SettingsRow> Settings => Set<SettingsRow>();
    public DbSet<AuditRow> Audit => Set<AuditRow>();
    public DbSet<TransactionRow> Transactions => Set<TransactionRow>();
    public DbSet<ImportRunRow> ImportRuns => Set<ImportRunRow>();
    public DbSet<ImportLinkRow> ImportLinks => Set<ImportLinkRow>();
    public DbSet<ReviewItemRow> ReviewItems => Set<ReviewItemRow>();
    public DbSet<RawRecordRow> RawRecords => Set<RawRecordRow>();

    public static ProfileDbContext For(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite(connection).Options);

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite can't order or compare DateTimeOffset text; store it as a sortable integer.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AccountRow>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).HasMaxLength(200);
            e.Property(a => a.Kind).HasMaxLength(40);
            e.Property(a => a.Currency).HasMaxLength(3);
        });

        model.Entity<BalanceRow>(e =>
        {
            e.ToTable("BalanceSnapshots");
            e.HasKey(b => b.Id);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(b => b.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(b => new { b.AccountId, b.AsOf });
            e.Property(b => b.Currency).HasMaxLength(3);
            e.Property(b => b.State).HasMaxLength(40);
        });

        model.Entity<BillRow>(e =>
        {
            e.ToTable("Bills");
            e.HasKey(b => b.Id);
            e.Property(b => b.Name).HasMaxLength(200);
            e.Property(b => b.Purpose).HasMaxLength(40);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(b => b.FundingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(b => b.TargetAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<IncomeRow>(e =>
        {
            e.ToTable("IncomeSchedules");
            e.HasKey(i => i.Id);
            e.Property(i => i.Name).HasMaxLength(200);
            e.Property(i => i.Currency).HasMaxLength(3);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(i => i.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<DebtRow>(e =>
        {
            e.ToTable("Debts");
            e.HasKey(d => d.AccountId);
            e.HasOne<AccountRow>().WithOne().HasForeignKey<DebtRow>(d => d.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<SettingsRow>(e =>
        {
            e.ToTable("PlanSettings");
            e.HasKey(s => s.Id);
            e.Property(s => s.Currency).HasMaxLength(3);
        });

        model.Entity<TransactionRow>(e =>
        {
            e.ToTable("Transactions");
            e.HasKey(t => t.Id);
            e.HasOne<AccountRow>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.AccountId, t.EffectiveDate });
            e.HasIndex(t => t.SourceKey).IsUnique();
            e.Property(t => t.Currency).HasMaxLength(3);
            e.Property(t => t.Status).HasMaxLength(40);
            e.Property(t => t.Description).HasMaxLength(400);
        });

        model.Entity<ImportRunRow>(e =>
        {
            e.ToTable("ImportRuns");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.At);
            e.Property(r => r.Mapper).HasMaxLength(80);
            e.Property(r => r.Origin).HasMaxLength(40);
        });

        model.Entity<ImportLinkRow>(e =>
        {
            e.ToTable("ImportLinks");
            e.HasKey(l => new { l.Entity, l.Key });
            e.Property(l => l.Entity).HasMaxLength(40);
            e.HasOne<ImportRunRow>().WithMany().HasForeignKey(l => l.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ReviewItemRow>(e =>
        {
            e.ToTable("ReviewItems");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.Fingerprint);
            e.HasIndex(r => r.Status);
            e.Property(r => r.Code).HasMaxLength(80);
            e.Property(r => r.Severity).HasMaxLength(40);
            e.Property(r => r.Status).HasMaxLength(40);
            e.HasOne<ImportRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RawRecordRow>(e =>
        {
            e.ToTable("RawRecords");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.RunId);
            e.Property(r => r.Kind).HasMaxLength(80);
            e.HasOne<ImportRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<AuditRow>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.At);
            e.Property(a => a.Action).HasMaxLength(80);
            e.Property(a => a.Entity).HasMaxLength(80);
        });
    }
}

// Lets `dotnet ef migrations add` build the model. It never opens a database.
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ProfileDbContext>
{
    public ProfileDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite("Data Source=design-time-only.db").Options);
}
