using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Modules.PlayerEnrichment;
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
}
