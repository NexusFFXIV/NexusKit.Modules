using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.InternalData.Persistence;

/// <summary>
/// Declares <c>nexus_filter_player</c>, the view the player-list filter pipeline
/// queries when a user filter touches Lodestone-side fields (gear score, FC
/// name, mount/minion/achievement counts, max job level, notes content). The
/// view LEFT-JOINs the observed-player base table against
/// <c>nexus_external_player[*]</c> and <c>nexus_external_free_company</c> so
/// unenriched characters still surface — their Lodestone columns just come
/// back as NULL, and the filter compiles into SQL that naturally rejects NULL
/// in numeric / text comparisons.
///
/// <para>Lives in the InternalData module because the consumer (player-list
/// filter) is internal-to-tracker. The view physically references the
/// ExternalData module's tables, but SQLite views don't enforce dependency
/// order at create time — the only constraint is that the underlying tables
/// must exist when the view is queried, which they always will once
/// <c>EnsureCreated</c> + module migrations have run.</para>
/// </summary>
internal sealed class PlayerFilterViewBuilder : IDatabaseViewBuilder
{
    private const string ViewName = "nexus_filter_player";

    public async Task BuildAsync(DbContext ctx, CancellationToken ct)
    {
        // DROP+CREATE on every startup so editing this builder is the only step
        // needed to change the view shape — no migration-id juggling. SQLite
        // views aren't materialized, so this is a metadata-only operation;
        // even on a multi-thousand-row DB it's sub-ms.
        await ctx.Database.ExecuteSqlRawAsync($"DROP VIEW IF EXISTS {ViewName};", ct)
            .ConfigureAwait(false);

        // Soul-crystal slot (index 12) is intentionally excluded from the gear
        // score, mirroring Models/Player.cs GearScore in the ExternalData
        // module (FFXIV item levels don't apply to soul crystals). Integer
        // division on purpose to match that implementation.
        const string sql = $"""
            CREATE VIEW {ViewName} AS
            SELECT
                o.content_id            AS content_id,
                o.lodestone_id          AS lodestone_id,
                o.name                  AS name,
                o.home_world_id         AS home_world_id,
                o.class_job_id          AS class_job_id,
                o.level                 AS level,
                o.online_status_id      AS online_status_id,
                o.company_tag           AS company_tag,
                o.notes                 AS notes,
                e.data_center_id        AS external_data_center_id,
                p.free_company_lodestone_id AS free_company_lodestone_id,
                fc.name                 AS fc_name,
                fc.tag                  AS fc_tag,
                fc.active_member_count  AS fc_active_member_count,
                cj.max_level            AS max_job_level,
                gs.gear_score           AS gear_score,
                cs.mount_count          AS mount_count,
                cs.minion_count         AS minion_count,
                cs.achievement_count    AS achievement_count,
                enc.last_encounter_at   AS last_encounter_at
            FROM nexus_internal_observed_player o
            LEFT JOIN nexus_external_player e
                ON e.lodestone_id = o.lodestone_id
            LEFT JOIN nexus_external_player_profile p
                ON p.lodestone_id = o.lodestone_id
            LEFT JOIN nexus_external_free_company fc
                ON fc.lodestone_id = p.free_company_lodestone_id
            LEFT JOIN (
                SELECT lodestone_id, MAX(level) AS max_level
                FROM nexus_external_player_class_job
                GROUP BY lodestone_id
            ) cj ON cj.lodestone_id = o.lodestone_id
            LEFT JOIN (
                SELECT lodestone_id,
                       SUM(item_level) / NULLIF(COUNT(item_level), 0) AS gear_score
                FROM nexus_external_player_gear_slot
                WHERE slot_index != 12 AND item_level IS NOT NULL
                GROUP BY lodestone_id
            ) gs ON gs.lodestone_id = o.lodestone_id
            LEFT JOIN (
                SELECT lodestone_id,
                       MAX(CASE WHEN kind = 0 THEN count END) AS mount_count,
                       MAX(CASE WHEN kind = 1 THEN count END) AS minion_count,
                       MAX(CASE WHEN kind = 2 THEN count END) AS achievement_count
                FROM nexus_external_player_collection_stats
                GROUP BY lodestone_id
            ) cs ON cs.lodestone_id = o.lodestone_id
            LEFT JOIN (
                SELECT content_id,
                       MAX(last_seen_at) AS last_encounter_at
                FROM nexus_internal_player_encounter
                GROUP BY content_id
            ) enc ON enc.content_id = o.content_id;
            """;

        await ctx.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
    }
}
