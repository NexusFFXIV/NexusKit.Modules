namespace NexusKit.Modules.InternalData.Encounters;

/// <summary>
/// One territory-bounded session of the local player. Opens lazily on the
/// first non-local sighting in a zone, closes on <c>TerritoryChanged</c> /
/// logout. Players visible during the session attach via
/// <see cref="InternalPlayerEncounterEntity"/>.
/// </summary>
public sealed class InternalEncounterEntity
{
    public long Id { get; set; }

    /// <summary>FFXIV territory (zone) row id from Lumina. Resolved to a name
    /// for display via <c>IGameDataLookups.GetTerritoryName</c>, or — for
    /// instanced content (Dungeons / Trials / Raids / Eureka / PvP / …) —
    /// preferentially via <c>IGameDataLookups.GetInstancedContentName</c>,
    /// which follows <c>TerritoryType.ContentFinderCondition</c> to surface the
    /// duty's display name instead of the underlying map's geography name and
    /// filters out CFC entries that only exist for city / housing engine
    /// plumbing.</summary>
    public ushort TerritoryTypeId { get; set; }

    /// <summary>Local player's <c>CurrentWorld</c> at encounter-open time —
    /// the world the encounter physically took place on. Captured from
    /// <c>PlayerObservationEvent.LocalPlayerCurrentWorldId</c>, which the
    /// watcher reads on the framework thread; reading it later off-thread
    /// (e.g. via <c>IObjectTable</c> inside the encounter tracker's async
    /// upsert) races with the engine and yields <c>null</c>, so the capture
    /// point matters. <c>null</c> on rows written before the column was
    /// introduced and on rows where the local player wasn't loaded at the
    /// moment a sighting was processed.</summary>
    public ushort? WorldId { get; set; }

    /// <summary>UTC timestamp when the first non-local sighting fired and the
    /// row was inserted. Earlier than the encounter's own zone-enter when the
    /// player stood alone for a while before someone showed up.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the local player left the territory or
    /// logged out. NULL while the encounter is still open; the startup
    /// orphan sweep stamps it with the latest <c>last_seen_at</c> of any
    /// linked player_encounter row when the prior session crashed.</summary>
    public DateTime? EndedAt { get; set; }
}
