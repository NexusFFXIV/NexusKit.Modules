namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Lazy-loaded sidecar for <see cref="ObservedPlayer"/>: the bulky fields the
/// hot path doesn't need but the detail panel and the Notes-content filter do.
/// Fetched via <see cref="IInternalDataPlayerWatcher.GetDetailAsync"/>.
/// </summary>
/// <param name="ContentId">Same key as the matching <see cref="ObservedPlayer"/>.</param>
/// <param name="FullCustomize">Full 26-byte FFXIV appearance array. Used by
/// <c>AvatarPlaceholder</c> for the selected character. Null when the row
/// hasn't been seen with a customize snapshot yet (pre-feature DBs, or rows
/// hydrated from a lodestone-only source).</param>
/// <param name="Notes">User-authored notes text. Null / empty when the user
/// hasn't written anything for this character.</param>
/// <param name="SearchComment">The character's own in-game search comment,
/// captured the last time the user examined them. Null when never captured or
/// when they have none set. Sits here rather than on <see cref="ObservedPlayer"/>
/// for the same reason as the notes: it is a per-character free-text field the
/// list never renders, and the hot record holds every observed row at once.
/// Not the Lodestone biography.</param>
public sealed record ObservedPlayerDetail(
    ulong ContentId,
    byte[]? FullCustomize,
    string? Notes,
    string? SearchComment);
