namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Slim in-memory projection of a character the local player has encountered
/// in-game. Produced by <see cref="IInternalDataPlayerWatcher"/>; combines what
/// the object table snapshot delivers (job, race/gender, mount/minion, online
/// status, tag) with session-level usage counters (FirstSeen / LastSeen).
/// <para>The heavy fields — full <c>Customize</c> byte array (26 B per row) and
/// the user-authored notes string — are NOT carried here. The watcher keeps a
/// <see cref="ObservedPlayerDetail"/> read on
/// <c>IInternalDataPlayerWatcher.GetDetailAsync</c>, called only when the detail
/// panel surfaces them. This keeps <c>mObserved</c> linear in row count but
/// independent of per-character note length and avoids holding 26 bytes of
/// appearance for the 99 % of players we don't render an avatar for.</para>
/// </summary>
/// <param name="ContentId">Stable cross-session character id.</param>
/// <param name="LodestoneId">Resolved Lodestone character id once enrichment
/// completes; null while still in flight or when no public profile exists.</param>
/// <param name="Name">In-game character name.</param>
/// <param name="HomeWorld">Home-world display name (already resolved via
/// <c>IGameDataLookups.GetWorldName</c>).</param>
/// <param name="HomeWorldId">Raw Lumina World row id. Kept alongside the
/// resolved name so callers that need to query other systems by id (e.g. the
/// FC tag-candidate lookup) don't have to reverse-resolve the localized
/// display string.</param>
/// <param name="ClassJobId">Currently-active Lumina <c>ClassJob.RowId</c>.</param>
/// <param name="Level">Level on the active job.</param>
/// <param name="Race">FFXIV race byte (Customize[0]). 0 when never observed.</param>
/// <param name="Gender">FFXIV gender byte (Customize[1]; 0 male, 1 female).</param>
/// <param name="CompanyTag">Free-company tag (null when not in an FC).</param>
/// <param name="CurrentMountId">Mount row currently summoned (null = none).</param>
/// <param name="CurrentMinionId">Minion row currently summoned (null = none).</param>
/// <param name="OnlineStatusId">Lumina <c>OnlineStatus.RowId</c>.</param>
/// <param name="FirstSeen">UTC timestamp of the first observation we have on
/// file. Hydrated at startup from
/// <c>MIN(player_encounter.first_seen_at)</c> per character; advanced in
/// memory by the watcher when this is the very first sighting in the running
/// session. Not persisted as a denormalized column — see the migration
/// <c>20260518_encounters_and_drop_observed_aggregates</c>.</param>
/// <param name="LastSeen">UTC timestamp of the most recent observation.
/// Hydrated from <c>MAX(player_encounter.last_seen_at)</c>; bumped to the
/// current tick on every observation. Drives the player-list sort.</param>
/// <param name="HasNotes">True when the persisted <c>observed_player.notes</c>
/// column has non-empty content. The actual text is loaded on demand via
/// <see cref="IInternalDataPlayerWatcher.GetDetailAsync"/>.</param>
public sealed record ObservedPlayer(
    ulong ContentId,
    ulong? LodestoneId,
    string Name,
    string HomeWorld,
    uint HomeWorldId,
    uint ClassJobId,
    byte Level,
    byte Race,
    byte Gender,
    string? CompanyTag,
    uint? CurrentMountId,
    uint? CurrentMinionId,
    uint OnlineStatusId,
    DateTime FirstSeen,
    DateTime LastSeen,
    bool HasNotes);
