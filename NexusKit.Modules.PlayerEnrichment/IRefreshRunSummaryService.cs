namespace NexusKit.Modules.PlayerEnrichment;

public interface IRefreshRunSummaryService
{
    RefreshRunSummary Start();
    void Record(RefreshOutcome outcome);
    RefreshRunSummary Complete();
    Task<RefreshRunSummary> CompleteAsync(CancellationToken ct = default);
    RefreshRunSummary? Latest { get; }
    string? ExportLatestJson();
    Task<int> PruneAsync(int keep = 20, CancellationToken ct = default);
}
