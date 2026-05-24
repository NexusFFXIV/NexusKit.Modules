namespace NexusKit.Modules.InternalData.History;

/// <summary>
/// One change-event row for a character. Returned by
/// <see cref="IInternalDataHistoryService.GetForContentIdAsync"/>.
/// </summary>
/// <param name="Id">Auto-increment primary key (stable per row).</param>
/// <param name="ContentId">Lumina ContentId of the character the change belongs to.</param>
/// <param name="Kind">Which kind of change this row records.</param>
/// <param name="ChangedAt">UTC moment the change was first observed.</param>
/// <param name="OldValue">Display string for the prior value (e.g. <c>"Old Name"</c>,
/// <c>"Twintania"</c>, <c>"Hyur · Female"</c>, <c>"ATRS"</c>). Null for kinds where
/// "the prior value didn't exist" (e.g. joining an FC for the first time).</param>
/// <param name="NewValue">Display string for the current value. Null when the kind
/// represents removal (e.g. leaving an FC and ending up tag-less).</param>
/// <param name="IsRead">True once the user has opened the History tab while this
/// row was present for the owning character. False on newly-written rows; flipped
/// to true in bulk by <see cref="IInternalDataHistoryService.MarkAllReadForContentIdAsync"/>.</param>
public sealed record PlayerHistoryEntry(
    long Id,
    ulong ContentId,
    PlayerHistoryKind Kind,
    DateTime ChangedAt,
    string? OldValue,
    string? NewValue,
    bool IsRead);
