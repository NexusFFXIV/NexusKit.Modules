namespace NexusKit.Modules.InternalData.Encounters;

/// <summary>
/// Public projection of one encounter row joined with the corresponding
/// player_encounter row, returned by
/// <see cref="IInternalDataEncounterTracker.GetForContentIdAsync"/>. Carries
/// everything the UI's Encounters tab needs without exposing the persistence
/// entities to consumers.
/// </summary>
/// <param name="Id">The <c>InternalPlayerEncounterEntity</c> row id —
/// stable identifier for the (encounter, sighted player) pair, useful for
/// UI keys.</param>
/// <param name="TerritoryTypeId">Zone the encounter happened in.</param>
/// <param name="WorldId">Local player's <c>CurrentWorld</c> at encounter-open
/// time — the world the encounter physically took place on. The UI compares
/// this against the local player's CURRENT world to decide whether to render
/// the zone alone (match → we're back where it happened) or prefixed with
/// data-center + server (mismatch → encounter was on a different world).
/// <c>null</c> for rows written before the column existed; the UI falls
/// back to the zone-only form for those.</param>
/// <param name="EncounterStartedAt">When the parent encounter row was opened
/// (first non-local sighting in this zone).</param>
/// <param name="EncounterEndedAt">When the local player left the zone. Null
/// while the encounter is still open — the UI substitutes
/// <see cref="LastSeenAt"/> for the duration calculation in that case.</param>
/// <param name="JobId">Lumina ClassJob.RowId at first sighting.</param>
/// <param name="Level">Job level at first sighting.</param>
/// <param name="FirstSeenAt">When the local player first saw THIS character
/// in this encounter.</param>
/// <param name="LastSeenAt">Last visibility tick. The UI's "Dauer" column
/// renders <c>LastSeenAt - FirstSeenAt</c> — the per-player visible window.</param>
public sealed record EncounterEntry(
    long Id,
    ushort TerritoryTypeId,
    ushort? WorldId,
    DateTime EncounterStartedAt,
    DateTime? EncounterEndedAt,
    uint JobId,
    byte Level,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);
