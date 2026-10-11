using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace NexusKit.Modules.Domain.Tests;

public sealed class DomainServiceTests
{
    [Fact]
    public void SnapshotDiff_NormalizesMissingValuesAndSortsFields()
    {
        var service = new PlayerSnapshotDiffService();
        var previous = new PlayerSnapshot(42, DateTime.UtcNow.AddMinutes(-1),
            new Dictionary<string, string?> { ["name"] = "Old", ["empty"] = "" });
        var current = new PlayerSnapshot(42, DateTime.UtcNow,
            new Dictionary<string, string?> { ["name"] = "New", ["added"] = "value" });

        var diff = service.Compare(previous, current);

        Assert.Equal(new[] { "added", "name" }, diff.Changes.Select(x => x.Field));
        Assert.Equal(SnapshotChangeKind.Added, diff.Changes[0].Kind);
        Assert.Equal(SnapshotChangeKind.Changed, diff.Changes[1].Kind);
    }

    [Fact]
    public void SnapshotDiff_RejectsDifferentCharacters()
    {
        var service = new PlayerSnapshotDiffService();
        var first = new PlayerSnapshot(1, DateTime.UtcNow, new Dictionary<string, string?>());
        var second = new PlayerSnapshot(2, DateTime.UtcNow, new Dictionary<string, string?>());

        Assert.Throws<ArgumentException>(() => service.Compare(first, second));
    }

    [Fact]
    public void RunSummary_AnonymizesAndCountsOutcomes()
    {
        var builder = new RefreshRunSummaryBuilder();
        builder.Add(new RefreshOutcome(123, RefreshCategory.Profile,
            RefreshOutcomeStatus.Succeeded, 1, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow,
            Array.Empty<RefreshSourceStatus>()));
        builder.Add(new RefreshOutcome(456, RefreshCategory.Gear,
            RefreshOutcomeStatus.Failed, 3, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow,
            new[] { new RefreshSourceStatus("lodestone", RefreshSourceState.Failed, "timeout") },
            "timeout", true));

        var summary = builder.Complete();

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Exhausted);
        Assert.DoesNotContain("123", summary.Items[0].ContentKey);
        Assert.Equal("timeout", summary.Items[1].ErrorCode);
    }

    [Fact]
    public void RunSummary_CountsAllOutcomesWhenDiagnosticItemsAreCapped()
    {
        var builder = new RefreshRunSummaryBuilder();
        for (var i = 0; i < 501; i++)
            builder.Add(new RefreshOutcome((ulong)i, RefreshCategory.Profile,
                RefreshOutcomeStatus.Succeeded, 1, DateTime.UtcNow, DateTime.UtcNow,
                Array.Empty<RefreshSourceStatus>()));

        var summary = builder.Complete();

        Assert.Equal(501, summary.Total);
        Assert.Equal(501, summary.Succeeded);
        Assert.Equal(500, summary.Items.Count);
    }

    [Fact]
    public void RefreshMetrics_RecordsOutcomeCountersWithoutPlayerIdentity()
    {
        var metrics = new RefreshMetricsCollector();
        var now = DateTime.UtcNow;
        metrics.Record(new RefreshOutcome(42, RefreshCategory.Profile,
            RefreshOutcomeStatus.Succeeded, 1, now, now.AddMilliseconds(12),
            Array.Empty<RefreshSourceStatus>()));
        metrics.Record(new RefreshOutcome(43, RefreshCategory.Profile,
            RefreshOutcomeStatus.Failed, 2, now, now.AddMilliseconds(8),
            Array.Empty<RefreshSourceStatus>(), "timeout", true));
        metrics.SetQueueLength(3);

        var snapshot = metrics.Snapshot();

        Assert.Equal(2, snapshot.Attempts);
        Assert.Equal(1, snapshot.Succeeded);
        Assert.Equal(1, snapshot.Failed);
        Assert.Equal(1, snapshot.Exhausted);
        Assert.Equal(3, snapshot.CurrentQueueLength);
        Assert.Equal(10, snapshot.AverageDurationMs, precision: 6);
    }

    [Fact]
    public void DefaultCategoryPolicyEnablesEveryCategoryIncludingUnknownValues()
    {
        var policy = new DefaultRefreshCategoryPolicy();

        foreach (var category in Enum.GetValues<RefreshCategory>())
            Assert.True(policy.IsEnabled(category));
    }

    [Fact]
    public async Task SnapshotMigrationIsIdempotentAndCreatesExpectedSchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new DbContext(options);
        var migrationType = typeof(PlayerSnapshot).Assembly.GetType(
            "NexusKit.Modules.InternalData.Persistence.AddPlayerSnapshotTable",
            throwOnError: true)!;
        var migration = Activator.CreateInstance(migrationType)!;
        var up = migrationType.GetMethod("UpAsync", BindingFlags.Instance | BindingFlags.Public)!;

        await (Task)up.Invoke(migration, new object[] { context, CancellationToken.None })!;
        await (Task)up.Invoke(migration, new object[] { context, CancellationToken.None })!;

        var columns = await context.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM pragma_table_info('nexus_internal_player_snapshot')")
            .ToListAsync();
        Assert.Contains("content_id", columns);
        Assert.Contains("captured_at", columns);
        Assert.Contains("values_json", columns);
    }
}
