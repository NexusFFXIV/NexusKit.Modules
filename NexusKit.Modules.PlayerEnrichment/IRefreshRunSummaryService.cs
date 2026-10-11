namespace NexusKit.Modules.PlayerEnrichment;

public interface IRefreshRunSummaryService
{
    RefreshRunSummary Start();
    void Record(RefreshOutcome outcome);
    RefreshRunSummary Complete();
    RefreshRunSummary? Latest { get; }
    string? ExportLatestJson();
}
