namespace NexusKit.Modules.InternalData.History;

/// <summary>
/// Read API for the append-only <c>nexus_internal_player_history</c> log.
/// Writes happen implicitly: the service subscribes to
/// <c>IInternalDataPlayerWatcher.ObservationProcessed</c> in its constructor
/// and persists a row per detected field change.
/// </summary>
public interface IInternalDataHistoryService
{
    /// <summary>Most recent change entries for a character, newest first.
    /// Limit defaults to 200 — matches the cap the old plugin used for
    /// per-character history reads.</summary>
    Task<IReadOnlyList<PlayerHistoryEntry>> GetForContentIdAsync(
        ulong contentId,
        int limit = 200,
        CancellationToken ct = default);

    /// <summary>Returns, per <c>ContentId</c>, the distinct <see cref="PlayerHistoryKind"/>s
    /// that have at least one row still flagged unread (<c>is_read = 0</c>). Drives the
    /// player-list yellow dot AND its hover-tooltip — once every row is read, the
    /// character drops out of the dictionary and the dot disappears until a new
    /// observation produces another row.</summary>
    Task<IReadOnlyDictionary<ulong, IReadOnlySet<PlayerHistoryKind>>> GetUnreadHistoryKindsByContentIdAsync(
        CancellationToken ct = default);

    /// <summary>Flags every history row for a character as read. Called when the
    /// History tab opens for that character. No-op when no unread rows exist.
    /// Fires <see cref="HistoryRead"/> on success.</summary>
    Task MarkAllReadForContentIdAsync(ulong contentId, CancellationToken ct = default);

    /// <summary>Flags every unread history row across <b>all</b> characters as read
    /// in a single DB roundtrip. No-op when nothing is unread. Fires
    /// <see cref="AllHistoryRead"/> on success (i.e. when at least one row was
    /// affected); the per-character <see cref="HistoryRead"/> is <i>not</i> fanned
    /// out — subscribers that need to react to either should listen to both.</summary>
    Task MarkAllReadAsync(CancellationToken ct = default);

    /// <summary>Appends a history row when the supplied <paramref name="newValue"/>
    /// differs from the <i>most recent</i> row for the same <c>(contentId, kind)</c> —
    /// or when no row of that kind exists yet. Designed for callers outside the
    /// live-observation pipeline (e.g. the Lodestone refresh path), which may see
    /// the same effective change a second time after the live watcher already
    /// logged it; the dedup avoids producing twin entries.
    /// <para>Returns the persisted entry on insert, or <c>null</c> when the row was
    /// suppressed as a duplicate. Fires <see cref="HistoryAdded"/> only on insert.</para>
    /// <para>NewValue/OldValue comparison is ordinal; treat null and empty as the
    /// same value so a Lodestone-refresh that read the stale cached null doesn't
    /// duplicate a previously-logged "—" → X transition.</para>
    /// </summary>
    Task<PlayerHistoryEntry?> InsertIfNewAsync(
        ulong contentId,
        PlayerHistoryKind kind,
        DateTime changedAt,
        string? oldValue,
        string? newValue,
        CancellationToken ct = default);

    /// <summary>Fires after history rows for a character are persisted. Carries the
    /// affected ContentId and the freshly-written rows so subscribers (UI cache, chat
    /// notifier) can react without re-querying. Handlers run on the worker thread
    /// that wrote the rows — UI code must marshal to the draw thread itself (a
    /// reference assignment of an IReadOnlyList field is enough in practice).</summary>
    event Action<ulong, IReadOnlyList<PlayerHistoryEntry>>? HistoryAdded;

    /// <summary>Fires after <see cref="MarkAllReadForContentIdAsync"/> persists, so
    /// the UI's unread index can drop the affected character without re-querying.</summary>
    event Action<ulong>? HistoryRead;

    /// <summary>Fires after <see cref="MarkAllReadAsync"/> persists at least one
    /// row. Carries no payload — the UI's unread index drops every entry. The
    /// per-character <see cref="HistoryRead"/> event is NOT fanned out.</summary>
    event Action? AllHistoryRead;
}
