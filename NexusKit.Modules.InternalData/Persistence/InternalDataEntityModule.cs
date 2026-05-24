using Microsoft.EntityFrameworkCore;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>
/// Persists in-game observations: who the local player saw, when, and what
/// their currently-active gear+status/customize looked like at observation time.
/// Names + worlds are stored as IDs alongside the rare displayable string
/// (<see cref="InternalObservedPlayerEntity.Name"/>) so the recent-players list
/// can render without going through Lumina on every frame.
/// </summary>
internal sealed class InternalDataEntityModule : IEntityModule
{
    public string TablePrefix => "internal";

    public void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InternalObservedPlayerEntity>(e =>
        {
            e.ToTable("observed_player");
            e.HasKey(x => x.ContentId);
            e.Property(x => x.ContentId).HasColumnName("content_id");
            e.Property(x => x.LodestoneId).HasColumnName("lodestone_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            e.Property(x => x.HomeWorldId).HasColumnName("home_world_id");
            e.Property(x => x.CurrentWorldId).HasColumnName("current_world_id");
            e.Property(x => x.ClassJobId).HasColumnName("class_job_id");
            e.Property(x => x.Level).HasColumnName("level");
            e.Property(x => x.Customize).HasColumnName("customize");
            e.Property(x => x.CompanyTag).HasColumnName("company_tag").HasMaxLength(8);
            e.Property(x => x.CurrentMountId).HasColumnName("current_mount_id");
            e.Property(x => x.CurrentMinionId).HasColumnName("current_minion_id");
            e.Property(x => x.OnlineStatusId).HasColumnName("online_status_id");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.LodestoneId);
            // The hydrate-time ordering by LastSeen was previously powered by an
            // index on this table's last_seen column; that column is gone (now
            // derived from player_encounter aggregates). Hydrate sorts in-memory
            // after merging the aggregate read — the row count is small enough
            // that this is cheap.
        });

        modelBuilder.Entity<InternalPlayerHistoryEntity>(e =>
        {
            e.ToTable("player_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.ContentId).HasColumnName("content_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.Property(x => x.OldValue).HasColumnName("old_value");
            e.Property(x => x.NewValue).HasColumnName("new_value");
            e.Property(x => x.IsRead).HasColumnName("is_read");

            // (content_id, changed_at DESC) is the primary read path — UI fetches
            // the newest N entries for a character. Kind index supports per-kind
            // filtering later (not used in iteration 1).
            e.HasIndex(x => new { x.ContentId, x.ChangedAt }).IsDescending(false, true);
            e.HasIndex(x => x.Kind);
            // The unread-only dot index sweeps WHERE is_read = 0, GROUP BY (content_id, kind).
            // Plain composite stays small and supports both startup bootstrap and per-character
            // mark-all-read writes.
            e.HasIndex(x => new { x.ContentId, x.IsRead });
        });

        modelBuilder.Entity<InternalRefreshQueueEntity>(e =>
        {
            e.ToTable("refresh_queue");
            e.HasKey(x => new { x.ContentId, x.Category });
            e.Property(x => x.ContentId).HasColumnName("content_id");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.EnqueuedAt).HasColumnName("enqueued_at");
            e.Property(x => x.LastAttemptedAt).HasColumnName("last_attempted_at");
            e.Property(x => x.LastFailedAt).HasColumnName("last_failed_at");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");

            // The worker's pick query is ORDER BY priority ASC, category ASC,
            // enqueued_at ASC, gated by last_failed_at < now - backoff.
            // Composite index keeps it an index-only scan as the queue grows;
            // (last_failed_at) supports the backoff filter.
            e.HasIndex(x => new { x.Priority, x.Category, x.EnqueuedAt });
            e.HasIndex(x => x.LastFailedAt);
        });

        modelBuilder.Entity<InternalEncounterEntity>(e =>
        {
            e.ToTable("encounter");
            e.HasKey(x => x.Id);
            // Property declaration order drives EnsureCreated's CREATE TABLE
            // column order on fresh installs: id, world_id, territory_type_id,
            // started_at, ended_at. Existing installs are normalised to the
            // same layout by AddEncounterWorldIdColumn.
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.WorldId).HasColumnName("world_id");
            e.Property(x => x.TerritoryTypeId).HasColumnName("territory_type_id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");

            // Recent-encounters scans walk started_at DESC.
            e.HasIndex(x => x.StartedAt).IsDescending();
        });

        modelBuilder.Entity<InternalPlayerEncounterEntity>(e =>
        {
            e.ToTable("player_encounter");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.EncounterId).HasColumnName("encounter_id");
            e.Property(x => x.ContentId).HasColumnName("content_id");
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.Level).HasColumnName("level");
            e.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");

            // Primary read path: "encounters for player X newest first" —
            // (content_id, first_seen_at DESC) keeps the Encounters tab's
            // initial load index-only.
            e.HasIndex(x => new { x.ContentId, x.FirstSeenAt }).IsDescending(false, true);
            // Join from encounter -> roster.
            e.HasIndex(x => x.EncounterId);
        });
    }
}
