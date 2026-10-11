using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>Bounded, privacy-safe summary of one refresh run.</summary>
public sealed record RefreshRunSummary(
    Guid RunId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    int Total,
    int Succeeded,
    int Failed,
    int Skipped,
    int Exhausted,
    IReadOnlyList<RefreshRunItem> Items);

public sealed record RefreshRunItem(
    string ContentKey,
    string Category,
    RefreshOutcomeStatus Status,
    int Attempt,
    string? ErrorCode,
    TimeSpan Duration,
    bool IsExhausted,
    IReadOnlyList<RefreshSourceStatus> Sources);

/// <summary>Collects outcomes and emits an anonymized JSON diagnostic export.
/// The item limit prevents a diagnostic action from producing unbounded output.</summary>
public sealed class RefreshRunSummaryBuilder
{
    private const int MaxItems = 500;
    private readonly Guid mRunId = Guid.NewGuid();
    private readonly DateTime mStartedAtUtc = DateTime.UtcNow;
    private readonly List<RefreshRunItem> mItems = new();
    private int mTotal;
    private int mSucceeded;
    private int mFailed;
    private int mSkipped;
    private int mExhausted;

    public void Add(RefreshOutcome outcome)
    {
        mTotal++;
        if (outcome.Status == RefreshOutcomeStatus.Succeeded) mSucceeded++;
        if (outcome.Status == RefreshOutcomeStatus.Failed) mFailed++;
        if (outcome.Status == RefreshOutcomeStatus.Skipped) mSkipped++;
        if (outcome.IsExhausted) mExhausted++;
        if (mItems.Count >= MaxItems) return;

        mItems.Add(new RefreshRunItem(
            HashContentId(outcome.ContentId),
            outcome.Category.ToString(),
            outcome.Status,
            outcome.Attempt,
            outcome.ErrorCode,
            outcome.Duration,
            outcome.IsExhausted,
            outcome.Sources));
    }

    public RefreshRunSummary Complete(DateTime? completedAtUtc = null)
    {
        var completed = completedAtUtc ?? DateTime.UtcNow;
        return new RefreshRunSummary(
            mRunId,
            mStartedAtUtc,
            completed,
            mTotal,
            mSucceeded,
            mFailed,
            mSkipped,
            mExhausted,
            mItems.ToArray());
    }

    public string ExportJson(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(Complete(), options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string HashContentId(ulong contentId)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BitConverter.TryWriteBytes(bytes, contentId);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
    }
}
