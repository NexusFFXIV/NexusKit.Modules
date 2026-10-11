using NexusKit.Modules.InternalData.Players;

namespace NexusKit.Modules.InternalData.History;

/// <summary>Keeps the two most recent normalized observation snapshots per
/// character for an in-session "since last scan" view. Raw IDs are stored for
/// localized fields so changing the game language cannot create a false diff.</summary>
public sealed class PlayerSnapshotStore : IDisposable
{
    private readonly IInternalDataPlayerWatcher mWatcher;
    private readonly object mGate = new();
    private readonly Dictionary<ulong, (PlayerSnapshot Previous, PlayerSnapshot Current)> mLatest = new();

    public PlayerSnapshotStore(IInternalDataPlayerWatcher watcher)
    {
        mWatcher = watcher;
        mWatcher.ObservationProcessed += OnObservationProcessed;
    }

    public bool TryGetDiff(ulong contentId, PlayerSnapshotDiffService diffService,
        out PlayerSnapshotDiff? diff)
    {
        lock (mGate)
        {
            if (!mLatest.TryGetValue(contentId, out var pair))
            {
                diff = null;
                return false;
            }
            diff = diffService.Compare(pair.Previous, pair.Current);
            return true;
        }
    }

    private void OnObservationProcessed(PlayerObservationEvent evt)
    {
        if (evt.Previous is null) return;
        var previous = ToSnapshot(evt.Previous, evt.Previous.LastSeen);
        var current = ToSnapshot(evt.Current, evt.ObservedAt);
        lock (mGate) mLatest[evt.Current.ContentId] = (previous, current);
    }

    private static PlayerSnapshot ToSnapshot(ObservedPlayer player, DateTime capturedAt)
        => new(player.ContentId, capturedAt, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["name"] = player.Name,
            ["home_world_id"] = player.HomeWorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["class_job_id"] = player.ClassJobId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["level"] = player.Level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["race"] = player.Race.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gender"] = player.Gender.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["company_tag"] = player.CompanyTag,
        });

    public void Dispose() => mWatcher.ObservationProcessed -= OnObservationProcessed;
}
