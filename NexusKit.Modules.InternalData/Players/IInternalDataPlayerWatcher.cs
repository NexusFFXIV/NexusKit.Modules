namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Watches the Dalamud object table on the framework thread for visible
/// players. Produces the in-memory "recent" feed plus an
/// <see cref="Observed"/> event per upsert; the persisted snapshot lives in
/// <c>nexus_internal_observed_player</c>.
/// <para>The watcher deliberately knows nothing about Lodestone or
/// FFXIVCollect — that bridge belongs in <c>NexusKit.Modules.PlayerEnrichment</c>,
/// which subscribes to <see cref="Observed"/> and calls
/// <see cref="SetLodestoneIdAsync"/> when it has resolved an id for a
/// content_id we hadn't seen on the Lodestone yet.</para>
/// </summary>
public interface IInternalDataPlayerWatcher
{
    /// <summary>Snapshot of all players observed this session, sorted by
    /// <see cref="ObservedPlayer.LastSeen"/> descending. Cheap to call every frame.</summary>
    IReadOnlyList<ObservedPlayer> Recent { get; }

    /// <summary>O(1) per-id lookup against the in-memory observed-player map.
    /// Used by the player-list panel's loop-flip optimisation: when a DB
    /// pre-filter returns a tiny ContentId set, iterating the set + this
    /// lookup is cheaper than iterating <see cref="Recent"/> and probing
    /// set membership.</summary>
    bool TryGetObserved(ulong contentId, out ObservedPlayer? player);

    /// <summary>ContentIds of players the local player can see in the game world
    /// right now (snapshot from the last framework-thread scan). Updated atomically;
    /// safe to query from the UI thread.</summary>
    IReadOnlySet<ulong> CurrentlyVisible { get; }

    /// <summary>Monotonically increasing counter that bumps whenever the
    /// in-memory <see cref="Recent"/> snapshot changes (initial hydrate, every
    /// observation tick that mutated the cache, and Lodestone-id resolution).
    /// Consumers use this as a memoization key so they can cache a derived view
    /// of the list until something actually moves — the value is monotonic, so
    /// a simple "previous != current" test is sufficient. Safe to read from any
    /// thread.</summary>
    long Revision { get; }

    /// <summary>Fires whenever an entry is added or its <c>LastSeen</c> updates.
    /// Handlers run on the framework thread.</summary>
    event Action<ObservedPlayer>? Observed;

    /// <summary>Fires after each per-character upsert with both the prior
    /// in-memory record (or null on first observation) and the freshly captured
    /// one. Used by <c>InternalDataHistoryService</c> to detect field changes
    /// without re-reading the DB. Handlers run on the thread-pool task that owns
    /// the observation persistence, NOT the framework thread.</summary>
    event Action<PlayerObservationEvent>? ObservationProcessed;

    /// <summary>Called by the enrichment layer once it has resolved a
    /// LodestoneId for a previously-unresolved <paramref name="contentId"/>.
    /// Persists the id to the observed-player row, updates the in-memory cache,
    /// and re-fires <see cref="Observed"/> so subscribers (UI, refresh queue)
    /// see the resolved record without waiting for the next scan tick.</summary>
    Task SetLodestoneIdAsync(ulong contentId, ulong lodestoneId, CancellationToken ct = default);

    /// <summary>Persists user-authored notes for an observed character. Pass null
    /// or whitespace to clear them. Returns false when no observed_player row
    /// exists for the id yet — the UI gates its editor on having an observation,
    /// so this is mainly a safety net. The row's <c>UpdatedAt</c> is left alone
    /// so notes saves don't shift the observation-freshness signal.</summary>
    Task<bool> UpdateNotesAsync(ulong contentId, string? notes, CancellationToken ct = default);

    /// <summary>Lazy-loads the heavy fields not carried on the in-memory
    /// <see cref="ObservedPlayer"/>: full Customize bytes and Notes content.
    /// Single indexed lookup; safe to call from the UI thread. Returns null
    /// when no observed_player row exists for the id.</summary>
    Task<ObservedPlayerDetail?> GetDetailAsync(ulong contentId, CancellationToken ct = default);
}
