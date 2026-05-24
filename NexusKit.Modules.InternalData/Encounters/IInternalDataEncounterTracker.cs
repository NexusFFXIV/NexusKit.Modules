namespace NexusKit.Modules.InternalData.Encounters;

/// <summary>
/// Tracks territory-bounded sessions of the local player and the non-local
/// characters visible during each. Subscribers learn about session edits via
/// <see cref="EncountersChanged"/>; the UI's Encounters tab reads from
/// <see cref="GetForContentIdAsync"/> on selection and re-reads on the
/// event.
/// </summary>
public interface IInternalDataEncounterTracker
{
    /// <summary>
    /// Returns the encounters in which the given sighted character appeared,
    /// newest first. Capped by <paramref name="limit"/>. Cheap indexed read
    /// (content_id, first_seen_at DESC).
    /// </summary>
    Task<IReadOnlyList<EncounterEntry>> GetForContentIdAsync(
        ulong contentId, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Count of player_encounter rows for the given character — replaces the
    /// retired <c>nexus_internal_observed_player.seen_count</c> column. Used
    /// by the Summary tab's "sightings" stat.
    /// </summary>
    Task<int> GetEncounterCountAsync(ulong contentId, CancellationToken ct = default);

    /// <summary>
    /// Fires whenever a player_encounter row was inserted or its
    /// <c>last_seen_at</c> advanced. UI subscribers compare the supplied
    /// content_id to the selected player and re-load on a match. The
    /// EncounterId is included so the consumer can dedupe within a single
    /// encounter if it wants.
    /// </summary>
    event Action<ulong, long>? EncountersChanged;
}
