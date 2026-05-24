using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Migrations;

namespace NexusKit.Modules.ExternalData.Persistence;

internal sealed class ExternalDataMigrations : IMigrationModule
{
    public string ModuleId => "nexuskit.modules.externaldata";

    public IReadOnlyList<IMigration> Migrations { get; } = new IMigration[]
    {
        new AddExternalPlayerNameWorldIndex(),
        new AddFreeCompanyTagWorldIndex(),
        new AddEstateAddressTableAndFcLink(),
        new DropFreeCompanyLegacyEstateColumns(),
        new ConvertFreeCompanyActiveStateToId(),
        new ReorderFreeCompanyActiveStateIdColumn(),
    };
}

internal sealed class AddExternalPlayerNameWorldIndex : IMigration
{
    public string Id => "20260515_external_player_name_world_idx";

    public Task UpAsync(DbContext ctx, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_external_player_name_home_world " +
            "ON nexus_external_player (name, home_world_id);",
            ct);
}

internal sealed class AddFreeCompanyTagWorldIndex : IMigration
{
    public string Id => "20260521_external_free_company_tag_world_idx";

    /// <summary>
    /// Non-unique index supporting the FreeCompanyTab tag-based candidate
    /// lookup. (tag, world_id) is intentionally not UNIQUE — multiple FCs on
    /// the same world can share a tag.
    /// </summary>
    public Task UpAsync(DbContext ctx, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_tag_world " +
            "ON nexus_external_free_company (tag, world_id);",
            ct);
}

internal sealed class AddEstateAddressTableAndFcLink : IMigration
{
    public string Id => "20260522_estate_address_table_and_fc_link";

    /// <summary>
    /// Introduces the <c>nexus_estate_address</c> table and adds the
    /// <c>estate_address_id</c> FK column on <c>nexus_external_free_company</c>.
    /// Leaves the legacy <c>estate_name</c>/<c>estate_plot</c>/<c>estate_greeting</c>
    /// columns intact — DbInspect normalizes them in a separate one-time pass,
    /// and a later migration drops them once verified.
    /// <para>EF Core's <see cref="EstateAddressEntityModule"/> covers the
    /// fresh-DB path via <c>EnsureCreatedAsync</c>; this migration handles the
    /// upgrade path for existing installations.</para>
    /// </summary>
    public Task UpAsync(DbContext ctx, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS nexus_estate_address (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NULL,
                greeting TEXT NULL,
                data_center_id INTEGER NULL,
                world_id INTEGER NULL,
                district_territory_id INTEGER NULL,
                ward INTEGER NULL,
                plot_number INTEGER NULL,
                is_apartment INTEGER NOT NULL DEFAULT 0,
                apartment_wing INTEGER NULL,
                house_size INTEGER NULL,
                is_subdivision INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_nexus_estate_address_world_district_ward_plot
                ON nexus_estate_address (world_id, district_territory_id, ward, plot_number);

            ALTER TABLE nexus_external_free_company ADD COLUMN estate_address_id INTEGER NULL REFERENCES nexus_estate_address(id);
            CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_estate_address
                ON nexus_external_free_company (estate_address_id);
        ", ct);
}

internal sealed class DropFreeCompanyLegacyEstateColumns : IMigration
{
    public string Id => "20260522_external_free_company_drop_legacy_estate_columns";

    /// <summary>
    /// Drops the three transitional estate columns (<c>estate_name</c>,
    /// <c>estate_plot</c>, <c>estate_greeting</c>) on
    /// <c>nexus_external_free_company</c> and repositions
    /// <c>estate_address_id</c> to sit where <c>estate_plot</c> used to live
    /// (between <c>grand_company_id</c> and <c>focus_role_play</c>) so the
    /// column order matches the C# entity declaration.
    /// <para>SQLite cannot reorder columns in place, so this migration does
    /// the standard table-rebuild dance: create a new table with the desired
    /// order, copy the data, drop the original, rename the temp into place,
    /// recreate indexes. FK checks are deferred to commit time so the
    /// existing references survive the swap.</para>
    /// </summary>
    public Task UpAsync(DbContext ctx, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(@"
            -- nexus_filter_player JOINs nexus_external_free_company. Dropping
            -- the table mid-swap leaves the view referencing a non-existent
            -- table; SQLite >= 3.25 validates dependent view definitions
            -- during ALTER TABLE RENAME and aborts. Drop the view here — the
            -- InternalData IDatabaseViewBuilder runs after every migration
            -- pass and DROP/CREATEs the view, so this is self-healing.
            DROP VIEW IF EXISTS nexus_filter_player;

            -- legacy_alter_table OFF would also try to rewrite the (already
            -- dropped) view's reference during RENAME; ON skips that step
            -- entirely, matching pre-3.25 semantics where renames don't
            -- propagate into view bodies.
            PRAGMA legacy_alter_table = ON;
            PRAGMA defer_foreign_keys = ON;

            CREATE TABLE _new_nexus_external_free_company (
                lodestone_id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                tag TEXT NULL,
                slogan TEXT NULL,
                formed_at TEXT NULL,
                world_id INTEGER NULL,
                rank INTEGER NULL,
                active_member_count INTEGER NULL,
                active_state TEXT NULL,
                recruitment_open INTEGER NULL,
                grand_company_id INTEGER NULL,
                estate_address_id INTEGER NULL REFERENCES nexus_estate_address(id) ON DELETE SET NULL,
                focus_role_play INTEGER NOT NULL,
                focus_leveling INTEGER NOT NULL,
                focus_casual INTEGER NOT NULL,
                focus_hardcore INTEGER NOT NULL,
                focus_dungeons INTEGER NOT NULL,
                focus_guildhests INTEGER NOT NULL,
                focus_trials INTEGER NOT NULL,
                focus_raids INTEGER NOT NULL,
                focus_pvp INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT INTO _new_nexus_external_free_company (
                lodestone_id, name, tag, slogan, formed_at, world_id, rank,
                active_member_count, active_state, recruitment_open, grand_company_id,
                estate_address_id,
                focus_role_play, focus_leveling, focus_casual, focus_hardcore,
                focus_dungeons, focus_guildhests, focus_trials, focus_raids, focus_pvp,
                updated_at)
            SELECT
                lodestone_id, name, tag, slogan, formed_at, world_id, rank,
                active_member_count, active_state, recruitment_open, grand_company_id,
                estate_address_id,
                focus_role_play, focus_leveling, focus_casual, focus_hardcore,
                focus_dungeons, focus_guildhests, focus_trials, focus_raids, focus_pvp,
                updated_at
            FROM nexus_external_free_company;

            DROP TABLE nexus_external_free_company;
            ALTER TABLE _new_nexus_external_free_company RENAME TO nexus_external_free_company;

            CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_tag_world
                ON nexus_external_free_company (tag, world_id);
            CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_estate_address
                ON nexus_external_free_company (estate_address_id);
        ", ct);
}

internal sealed class ConvertFreeCompanyActiveStateToId : IMigration
{
    public string Id => "20260523_external_free_company_active_state_id";

    /// <summary>
    /// Replaces the locale-mixed <c>active_state TEXT</c> column with a typed
    /// <c>active_state_id INTEGER NOT NULL DEFAULT 0</c> column carrying the
    /// <c>NexusKit.Modules.Lodestone.Models.FreeCompanyActiveState</c> enum
    /// (Unknown=0 / NotSpecified=1 / Always=2 / Weekdays=3 / Weekends=4). The
    /// inline backfill handles installs that skipped the DbInspect one-off;
    /// if DbInspect already ran (id column present, text column gone), the
    /// guard skips the whole migration body.
    /// <para>No Lumina sheet exists for FC active state — this is a Lodestone
    /// classification only, so the string set must stay in sync with
    /// <c>LodestoneClient.TryReadActiveState</c>.</para>
    /// </summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        var hasNewCol = await HasColumnAsync(ctx, "nexus_external_free_company", "active_state_id", ct)
            .ConfigureAwait(false);
        var hasOldCol = await HasColumnAsync(ctx, "nexus_external_free_company", "active_state", ct)
            .ConfigureAwait(false);

        if (hasNewCol && !hasOldCol)
        {
            // DbInspect already normalized the data — nothing to do.
            return;
        }

        if (!hasNewCol)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "ALTER TABLE nexus_external_free_company " +
                "ADD COLUMN active_state_id INTEGER NOT NULL DEFAULT 0;",
                ct).ConfigureAwait(false);
        }

        if (hasOldCol)
        {
            // Best-effort inline backfill — same string set as
            // FreeCompanyActiveStateMapper. Anything else stays 0 (Unknown).
            await ctx.Database.ExecuteSqlRawAsync(@"
                UPDATE nexus_external_free_company SET active_state_id = 1
                    WHERE active_state IN ('Not specified','Keine Angabe','Non spécifié','指定なし');
                UPDATE nexus_external_free_company SET active_state_id = 2
                    WHERE active_state IN ('Always','Jeden Tag','Tous les jours','毎日');
                UPDATE nexus_external_free_company SET active_state_id = 3
                    WHERE active_state IN ('Weekdays','Wochentags','En semaine','平日');
                UPDATE nexus_external_free_company SET active_state_id = 4
                    WHERE active_state IN ('Weekends','Wochenende','Le week-end','週末');
                ALTER TABLE nexus_external_free_company DROP COLUMN active_state;
            ", ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasColumnAsync(DbContext ctx, string table, string column, CancellationToken ct)
    {
        // pragma_table_info is a table-valued function; EF Core's parameter
        // binding can't substitute table/column identifiers, so we inline them
        // from compile-time literals (no untrusted input flows here).
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result ?? 0L) > 0;
    }
}

internal sealed class ReorderFreeCompanyActiveStateIdColumn : IMigration
{
    public string Id => "20260523_external_free_company_active_state_id_reorder";

    /// <summary>
    /// <see cref="ConvertFreeCompanyActiveStateToId"/> appended
    /// <c>active_state_id</c> at the end of the table (SQLite ADD COLUMN can
    /// only append). This migration rebuilds the table so the column sits
    /// where the legacy <c>active_state</c> TEXT used to live — between
    /// <c>active_member_count</c> and <c>recruitment_open</c> — matching the
    /// C# entity declaration order.
    /// <para>Fresh installs created via <c>EnsureCreatedAsync</c> already have
    /// the column in position; the guard skips the rebuild in that case.</para>
    /// </summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct).ConfigureAwait(false);

        int? cidActive;
        int? cidRecruit;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, cid FROM pragma_table_info('nexus_external_free_company') " +
                "WHERE name IN ('active_state_id','recruitment_open');";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                map[r.GetString(0)] = r.GetInt32(1);
            cidActive = map.TryGetValue("active_state_id", out var a) ? a : (int?)null;
            cidRecruit = map.TryGetValue("recruitment_open", out var b) ? b : (int?)null;
        }

        if (cidActive is null)
        {
            // No active_state_id column at all — ConvertFreeCompanyActiveStateToId
            // didn't run, so nothing to reorder yet.
            return;
        }
        if (cidRecruit is { } rc && cidActive < rc)
        {
            // Already left of recruitment_open → in or near the target slot.
            return;
        }

        await ctx.Database.ExecuteSqlRawAsync(@"
            -- nexus_filter_player joins this table; views block ALTER RENAME
            -- in SQLite ≥ 3.25. Drop and let InternalData IDatabaseViewBuilder
            -- recreate it after migrations (self-healing).
            DROP VIEW IF EXISTS nexus_filter_player;

            PRAGMA legacy_alter_table = ON;
            PRAGMA defer_foreign_keys = ON;

            CREATE TABLE _new_nexus_external_free_company (
                lodestone_id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                tag TEXT NULL,
                slogan TEXT NULL,
                formed_at TEXT NULL,
                world_id INTEGER NULL,
                rank INTEGER NULL,
                active_member_count INTEGER NULL,
                active_state_id INTEGER NOT NULL DEFAULT 0,
                recruitment_open INTEGER NULL,
                grand_company_id INTEGER NULL,
                estate_address_id INTEGER NULL REFERENCES nexus_estate_address(id) ON DELETE SET NULL,
                focus_role_play INTEGER NOT NULL,
                focus_leveling INTEGER NOT NULL,
                focus_casual INTEGER NOT NULL,
                focus_hardcore INTEGER NOT NULL,
                focus_dungeons INTEGER NOT NULL,
                focus_guildhests INTEGER NOT NULL,
                focus_trials INTEGER NOT NULL,
                focus_raids INTEGER NOT NULL,
                focus_pvp INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT INTO _new_nexus_external_free_company (
                lodestone_id, name, tag, slogan, formed_at, world_id, rank,
                active_member_count, active_state_id, recruitment_open,
                grand_company_id, estate_address_id,
                focus_role_play, focus_leveling, focus_casual, focus_hardcore,
                focus_dungeons, focus_guildhests, focus_trials, focus_raids, focus_pvp,
                updated_at)
            SELECT
                lodestone_id, name, tag, slogan, formed_at, world_id, rank,
                active_member_count, active_state_id, recruitment_open,
                grand_company_id, estate_address_id,
                focus_role_play, focus_leveling, focus_casual, focus_hardcore,
                focus_dungeons, focus_guildhests, focus_trials, focus_raids, focus_pvp,
                updated_at
            FROM nexus_external_free_company;

            DROP TABLE nexus_external_free_company;
            ALTER TABLE _new_nexus_external_free_company RENAME TO nexus_external_free_company;

            CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_tag_world
                ON nexus_external_free_company (tag, world_id);
            CREATE INDEX IF NOT EXISTS ix_nexus_external_free_company_estate_address
                ON nexus_external_free_company (estate_address_id);
        ", ct).ConfigureAwait(false);
    }
}
