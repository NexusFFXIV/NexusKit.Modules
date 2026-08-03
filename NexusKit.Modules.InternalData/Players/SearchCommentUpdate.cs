namespace NexusKit.Modules.InternalData.Players;

/// <summary>
/// Outcome of <see cref="IInternalDataPlayerWatcher.SetSearchCommentAsync"/>.
/// Carries the previous value so the caller can log a history row without a
/// second read — the write already had the old value in hand.
/// </summary>
/// <param name="Applied">False when nothing was written: no observation row for
/// this character, or the write failed. Distinct from a no-op write, where the
/// value simply already matched.</param>
/// <param name="ValueChanged">True only when the stored value actually moved.
/// Re-examining somebody whose comment is unchanged lands here as false, which
/// is what keeps repeat examines out of the history.</param>
/// <param name="Previous">The value on file before the write. Null when there
/// was none — that is the "search comment set for the first time" case.</param>
/// <param name="Current">The value on file after the write.</param>
public readonly record struct SearchCommentUpdate(
    bool Applied,
    bool ValueChanged,
    string? Previous,
    string? Current)
{
    public static SearchCommentUpdate NotApplied => new(false, false, null, null);

    public static SearchCommentUpdate Unchanged(string? value) => new(true, false, value, value);

    public static SearchCommentUpdate Changed(string? previous, string? current)
        => new(true, true, previous, current);
}
