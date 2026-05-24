namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Fired by <see cref="IInternalDataPlayerWatcher.ObservationProcessed"/> after each
/// upsert tick — both the prior in-memory ObservedPlayer (if any) and the freshly
/// captured one are handed to subscribers so they can diff without re-reading the DB.
/// </summary>
/// <param name="Previous">The ObservedPlayer record that was in the in-memory map
/// before this tick, or null when this is the character's first observation.</param>
/// <param name="Current">The ObservedPlayer record just persisted.</param>
/// <param name="ObservedAt">UTC timestamp the watcher recorded for this tick.</param>
/// <param name="CanTrackChange">True when the local client is in a context where
/// transient-prone fields (notably <c>CompanyTag</c>) from the live ObjectTable
/// can be trusted — i.e. NOT in a duty, cutscene, or zoning transition. False
/// during those contexts, where the game hides FC tags and snapshots are unreliable.
/// Diff subscribers should gate writes for transient fields on this flag; stable
/// fields (name, home world, race/gender) tick unconditionally.</param>
/// <param name="LocalPlayerCurrentWorldId">The local player's
/// <c>CurrentWorld</c> row id at the moment this tick was captured on the
/// framework thread, or <c>null</c> when the local player wasn't loaded
/// (zoning, between characters). Surfaced here because subscribers run from
/// the watcher's worker-thread dispatch and can't safely read
/// <c>IObjectTable</c> themselves — every value they need from the local
/// player has to be snapshotted before the off-thread hop.</param>
public sealed record PlayerObservationEvent(
    ObservedPlayer? Previous,
    ObservedPlayer Current,
    DateTime ObservedAt,
    bool CanTrackChange,
    uint? LocalPlayerCurrentWorldId);
