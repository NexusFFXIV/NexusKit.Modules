namespace NexusKit.Modules.ExternalData.Players;

/// <summary>
/// Side-effect sink that <see cref="ExternalDataPlayerService"/> calls when a
/// Lodestone-driven upsert observes a change to a player's identity or FC link.
/// Implementations (typically in InternalData, where the player-history log
/// lives) decide what to do with the signal — persist a history row,
/// re-evaluate observation filters, fire a chat notification, etc.
/// <para>The contract is fire-and-forget: ExternalData neither awaits a return
/// value nor reacts to exceptions; the recorder must own its own error handling
/// and never let a slow or failing recorder block the upsert path.</para>
/// <para>ExternalData has no reverse dependency on InternalData by design —
/// this interface is the only seam. Registration is optional; when no recorder
/// is wired, the player-service silently skips the notifications.</para>
/// </summary>
public interface IPlayerChangeRecorder
{
    /// <summary>
    /// Called from inside the upsert path after the diff has been detected but
    /// before <c>SaveChangesAsync</c> commits the new row. Implementations must
    /// not assume the DB row reflects <paramref name="newValue"/> yet.
    /// </summary>
    /// <param name="lodestoneId">Stable Lodestone character id — implementations
    /// resolve this to a content_id themselves if they need one.</param>
    /// <param name="kind">Which field changed (name, home world, FC link).</param>
    /// <param name="oldValue">Previous value (pre-formatted as the string the
    /// history log expects — e.g. world *names* not ids, so dedup against the
    /// live-watcher path works).</param>
    /// <param name="newValue">New value, same formatting rules as oldValue.</param>
    /// <param name="detectedAt">UTC timestamp of the detection.</param>
    /// <param name="ct">Co-operative cancellation. Implementations should
    /// respect it but never let cancellation propagate back into the upsert
    /// path — see the fire-and-forget remark on the interface.</param>
    Task RecordPlayerChangeAsync(
        ulong lodestoneId,
        PlayerChangeKind kind,
        string? oldValue,
        string? newValue,
        DateTime detectedAt,
        CancellationToken ct = default);
}

/// <summary>
/// Coarse-grained categorisation of Lodestone-detected player changes. Maps to
/// the InternalData <c>PlayerHistoryKind</c> enum in the recorder implementation
/// — kept distinct so ExternalData doesn't take a hard dependency on the history
/// kind set.
/// </summary>
public enum PlayerChangeKind
{
    /// <summary>Lodestone character name changed.</summary>
    Name = 1,

    /// <summary>Home world changed (transfer).</summary>
    HomeWorld = 2,

    /// <summary>Free Company link on the Lodestone profile changed (join, leave,
    /// switch). Carries old/new FC Lodestone id, not the tag.</summary>
    FreeCompany = 3,
}
