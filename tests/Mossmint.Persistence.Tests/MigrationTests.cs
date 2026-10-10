using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mossmint.Persistence.Data;

namespace Mossmint.Persistence.Tests;

public sealed class MigrationTests
{
    // Every schema change ships as a migration (AGENTS.md). A model edited
    // without one would fail on every existing profile at runtime.
    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var db = new DesignTimeFactory().CreateDbContext([]);

        db.Database.HasPendingModelChanges().Should().BeFalse("run `dotnet ef migrations add` for the change");
    }

    // Positive control: the comparison above runs against the real migration
    // snapshot, not an empty one that would trivially match nothing.
    [Fact]
    public void The_comparison_uses_the_real_migration_snapshot()
    {
        using var db = new DesignTimeFactory().CreateDbContext([]);

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;

        snapshot.Should().NotBeNull();
        snapshot!.Model.GetEntityTypes().Select(e => e.GetTableName())
            .Should().Contain(["Accounts", "BalanceSnapshots", "ReviewItems", "ImportRuns"]);
    }
}
