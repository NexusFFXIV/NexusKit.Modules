using NexusKit.Modules.InternalData.Refresh;

namespace NexusKit.Modules.PlayerEnrichment;

/// <summary>Terminal result of one queued refresh attempt.</summary>
public enum RefreshOutcomeStatus : byte
{
    Succeeded = 0,
    Failed = 1,
    Skipped = 2,
}

/// <summary>State of a source consulted by a refresh attempt.</summary>
public enum RefreshSourceState : byte
{
    NotAttempted = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>Source-level evidence attached to a refresh result.</summary>
public sealed record RefreshSourceStatus(
    string Source,
    RefreshSourceState State,
    string? ErrorCode = null);

/// <summary>
/// Stable, Dalamud-free result contract for queue consumers. The queue keeps
/// its existing events for compatibility and additionally publishes this
/// richer outcome for diagnostics, run summaries and UI status.
/// </summary>
public sealed record RefreshOutcome(
    ulong ContentId,
    RefreshCategory Category,
    RefreshOutcomeStatus Status,
    int Attempt,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    IReadOnlyList<RefreshSourceStatus> Sources,
    string? ErrorCode = null,
    bool IsExhausted = false)
{
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
}
