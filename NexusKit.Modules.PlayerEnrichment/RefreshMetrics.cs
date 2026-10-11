namespace NexusKit.Modules.PlayerEnrichment;

public sealed record RefreshMetricsSnapshot(
    long Attempts,
    long Succeeded,
    long Failed,
    long Skipped,
    long Exhausted,
    TimeSpan TotalDuration,
    int CurrentQueueLength)
{
    public double AverageDurationMs => Attempts == 0
        ? 0
        : TotalDuration.TotalMilliseconds / Attempts;
}

/// <summary>Small in-memory aggregate for diagnostics. It intentionally holds
/// counters only, never ContentIds or response payloads.</summary>
public sealed class RefreshMetricsCollector
{
    private readonly object mGate = new();
    private long mAttempts;
    private long mSucceeded;
    private long mFailed;
    private long mSkipped;
    private long mExhausted;
    private TimeSpan mDuration;
    private int mQueueLength;

    public void Record(RefreshOutcome outcome)
    {
        lock (mGate)
        {
            mAttempts++;
            mDuration += outcome.Duration;
            switch (outcome.Status)
            {
                case RefreshOutcomeStatus.Succeeded: mSucceeded++; break;
                case RefreshOutcomeStatus.Failed: mFailed++; break;
                case RefreshOutcomeStatus.Skipped: mSkipped++; break;
            }
            if (outcome.IsExhausted) mExhausted++;
        }
    }

    public void SetQueueLength(int length)
    {
        lock (mGate) mQueueLength = Math.Max(0, length);
    }

    public RefreshMetricsSnapshot Snapshot()
    {
        lock (mGate)
        {
            return new RefreshMetricsSnapshot(
                mAttempts, mSucceeded, mFailed, mSkipped, mExhausted,
                mDuration, mQueueLength);
        }
    }
}
