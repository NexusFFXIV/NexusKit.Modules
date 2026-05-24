namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Per-sub-resource <c>UpdatedAt</c> snapshot for a Lodestone-enriched player.
/// Each field is the latest persisted timestamp for that category, or null when
/// the row doesn't exist (never fetched). Used by the detail-panel header
/// tooltip so the user can see exactly which sub-resources are fresh and which
/// are stale — the "Updated Xh ago" text alone hides the variance.
/// </summary>
/// <param name="ProfileAt">Lodestone profile (bio, deity, FC link, …).</param>
/// <param name="ClassJobsAt">Newest UpdatedAt across the player's class-job rows.</param>
/// <param name="GearAt">Newest UpdatedAt across the player's gear slots.</param>
/// <param name="FreeCompanyAt">FC catalog row's UpdatedAt — null when the
/// player has no FC link or the catalog row hasn't been fetched yet.</param>
/// <param name="MountsAt">Collection-stats row for mounts.</param>
/// <param name="MinionsAt">Collection-stats row for minions.</param>
/// <param name="AchievementsAt">Collection-stats row for achievements.</param>
public sealed record PlayerRefreshBreakdown(
    DateTime? ProfileAt,
    DateTime? ClassJobsAt,
    DateTime? GearAt,
    DateTime? FreeCompanyAt,
    DateTime? MountsAt,
    DateTime? MinionsAt,
    DateTime? AchievementsAt);
