namespace NexusKit.Modules.PlayerEnrichment;

public sealed class RefreshRunSummaryService : IRefreshRunSummaryService, IDisposable
{
    private readonly IPlayerRefreshQueueService mQueue;
    private readonly RefreshMetricsCollector mMetrics;
    private RefreshRunSummaryBuilder? mBuilder;

    public RefreshRunSummaryService(IPlayerRefreshQueueService queue, RefreshMetricsCollector metrics)
    {
        mQueue = queue;
        mMetrics = metrics;
        mQueue.OutcomeProduced += Record;
    }

    public RefreshRunSummary? Latest { get; private set; }

    public RefreshRunSummary Start()
    {
        mBuilder = new RefreshRunSummaryBuilder();
        return mBuilder.Complete();
    }

    public void Record(RefreshOutcome outcome)
    {
        mMetrics.Record(outcome);
        mBuilder?.Add(outcome);
    }

    public RefreshRunSummary Complete()
    {
        var builder = mBuilder ?? new RefreshRunSummaryBuilder();
        Latest = builder.Complete();
        mBuilder = null;
        return Latest;
    }

    public string? ExportLatestJson()
        => Latest is null ? null : System.Text.Json.JsonSerializer.Serialize(Latest);

    public void Dispose() => mQueue.OutcomeProduced -= Record;
}
