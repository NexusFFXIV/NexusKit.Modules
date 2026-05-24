using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Migrations;

namespace NexusKit.Modules.InternalData.Persistence;

internal static class SqliteDateTimeFormats
{
    // Matches Microsoft.Data.Sqlite's default DateTime serialization
    // (capital F → trailing zeros omitted). Raw-SQL writes that bypass EF
    // Core have to mirror this format or the resulting rows mis-compare
    // lexicographically against EF-written rows (' ' < 'T'). The bug that
    // motivated this constant lived in the refresh-queue stats query — a
    // T-formatted @cutoff against space-formatted last_failed_at silently
    // classified every cooldown row as eligible.
    public const string ProviderFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";
}

internal sealed class InternalDataMigrations : IMigrationModule
{
    public string ModuleId => "nexuskit.modules.internaldata";

    public IReadOnlyList<IMigration> Migrations { get; } = new IMigration[]
    {
        new AddObservedPlayerLastSeenIndex(),
        new AddRefreshQueueTable(),
        new RebuildRefreshQueueOnContentId(),
        new AddPlayerHistoryIsReadColumn(),
        new AddEncountersDropSeenCount(),
        new AddObservedPlayerNotesColumn(),
        new AddEncounterWorldIdColumn(),
    };
}

internal sealed class AddEncounterWorldIdColumn : IMigration
{
    public string Id => "20260523_encounter_world_id_column";

    /// <summary>Adds (or relocates) the nullable <c>world_id</c> column on
    /// <c>nexus_internal_encounter</c> so the Encounters UI can compare the
    /// world the encounter happened on against the local player's current
    /// world. The column lives in the SECOND physical slot (right after
    /// <c>id</c>) — matching the entity-module declaration order — so it
    /// shows up as <c>id, world_id, territory_type_id, started_at,
    /// ended_at</c> in DB inspectors.
    /// <para>Three code paths driven by the current schema state, decided
    /// from <c>PRAGMA table_info</c>:</para>
    /// <list type="number">
    /// <item><b>Column already at slot 1</b> (fresh install via
    /// EnsureCreated, or this migration ran on a previous load): no-op.</item>
    /// <item><b>Column exists at the end</b> (an earlier iteration of this
    /// migration appended it via <c>ALTER TABLE ADD COLUMN</c>): full rebuild
    /// to reorder.</item>
    /// <item><b>Column missing entirely</b> (genuine upgrade from a pre-
    /// world_id schema): rebuild and add it in the correct slot, source
    /// values default to NULL.</item>
    /// </list>
    /// <para>The rebuild used to lean on a rename-swap with
    /// <c>PRAGMA legacy_alter_table = ON</c>, but in
    /// Microsoft.Data.Sqlite + EF Core's wrapping transaction that PRAGMA
    /// didn't actually suppress the FK rewrite — the swap propagated the
    /// rename into <c>nexus_internal_player_encounter.encounter_id</c>'s
    /// FK target, leaving it pointing at <c>nexus_internal_encounter_old</c>
    /// (which was then dropped). New strategy: rebuild BOTH the parent and
    /// the child. Dropping the child first removes every FK reference, so
    /// the parent can then be dropped cleanly without any PRAGMA tricks;
    /// the child is recreated against the rebuilt parent with a fresh FK
    /// clause that's guaranteed to point at the canonical name.</para>
    /// <para>Existing rows are staged into TEMP tables so the rebuild
    /// doesn't lose data — TEMP tables don't carry FK constraints, so they
    /// survive the parent/child drops untouched.</para></summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        // Read the current physical column order on the parent. PRAGMA
        // table_info returns rows in (cid, name, type, notnull, dflt_value,
        // pk) — name is at index 1 and the iteration is already in column
        // order.
        var columnNames = new List<string>();
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(nexus_internal_encounter);";
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columnNames.Add(reader.GetString(1));
        }

        // No-op when the column is already where we want it (slot 1, right
        // after id). Fresh installs land here because EnsureCreated already
        // used the entity-module's Property order.
        if (columnNames.Count >= 2
            && string.Equals(columnNames[1], "world_id", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hasWorldId = columnNames.Any(n => string.Equals(n, "world_id", StringComparison.OrdinalIgnoreCase));

        // 1. Stage both tables into TEMP. TEMP tables are FK-free so they
        // survive the parent/child drops below.
        if (hasWorldId)
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "CREATE TEMP TABLE _stash_encounter AS " +
                "SELECT id, world_id, territory_type_id, started_at, ended_at FROM nexus_internal_encounter;",
                ct).ConfigureAwait(false);
        }
        else
        {
            // Pre-world_id schema — synthesize the column as NULL so the
            // rest of the migration can use a single static INSERT shape.
            await ctx.Database.ExecuteSqlRawAsync(
                "CREATE TEMP TABLE _stash_encounter AS " +
                "SELECT id, NULL AS world_id, territory_type_id, started_at, ended_at FROM nexus_internal_encounter;",
                ct).ConfigureAwait(false);
        }
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TEMP TABLE _stash_player_encounter AS " +
            "SELECT id, encounter_id, content_id, job_id, level, first_seen_at, last_seen_at FROM nexus_internal_player_encounter;",
            ct).ConfigureAwait(false);

        // 2. Drop child first (no grandchild references it from elsewhere
        // — safe regardless of foreign_keys enforcement), then parent (no
        // children left to reference it).
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP TABLE nexus_internal_player_encounter;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP TABLE nexus_internal_encounter;", ct).ConfigureAwait(false);

        // 3. Recreate parent with the desired column layout.
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE nexus_internal_encounter (" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  world_id INTEGER NULL," +
            "  territory_type_id INTEGER NOT NULL," +
            "  started_at TEXT NOT NULL," +
            "  ended_at TEXT NULL);",
            ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_encounter_started_at " +
            "ON nexus_internal_encounter (started_at DESC);",
            ct).ConfigureAwait(false);

        // 4. Restore parent rows. Explicit id keeps child encounter_id refs
        // valid; AUTOINCREMENT picks up from MAX(id) on the next live insert.
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO nexus_internal_encounter (id, world_id, territory_type_id, started_at, ended_at) " +
            "SELECT id, world_id, territory_type_id, started_at, ended_at FROM _stash_encounter;",
            ct).ConfigureAwait(false);

        // 5. Recreate child with a FRESH FK clause referencing the rebuilt
        // parent by its canonical name. No rename-derived target name in
        // sight.
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE nexus_internal_player_encounter (" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  encounter_id INTEGER NOT NULL REFERENCES nexus_internal_encounter(id) ON DELETE CASCADE," +
            "  content_id INTEGER NOT NULL," +
            "  job_id INTEGER NOT NULL," +
            "  level INTEGER NOT NULL," +
            "  first_seen_at TEXT NOT NULL," +
            "  last_seen_at TEXT NOT NULL);",
            ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_player_encounter_content_id " +
            "ON nexus_internal_player_encounter (content_id, first_seen_at DESC);",
            ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_player_encounter_encounter_id " +
            "ON nexus_internal_player_encounter (encounter_id);",
            ct).ConfigureAwait(false);

        // 6. Restore child rows.
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO nexus_internal_player_encounter (id, encounter_id, content_id, job_id, level, first_seen_at, last_seen_at) " +
            "SELECT id, encounter_id, content_id, job_id, level, first_seen_at, last_seen_at FROM _stash_player_encounter;",
            ct).ConfigureAwait(false);

        // 7. Drop the stash tables. TEMP tables would die with the
        // connection anyway, but explicit cleanup keeps the post-migration
        // schema tidy and avoids any chance of confusion if the connection
        // is reused.
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP TABLE _stash_player_encounter;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP TABLE _stash_encounter;", ct).ConfigureAwait(false);
    }
}

internal sealed class AddObservedPlayerNotesColumn : IMigration
{
    public string Id => "20260520_observed_player_notes_column";

    // Fresh installs go through EnsureCreated, which already has the notes column
    // because the entity module declares it — so this migration is recorded as
    // applied on baseline and never runs. Upgrade installs hit the ALTER TABLE
    // below; PRAGMA-gated so a partial re-run is idempotent.
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(nexus_internal_observed_player);";
            await using var reader = await probe.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "notes", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        await ctx.Database.ExecuteSqlRawAsync(
            "ALTER TABLE nexus_internal_observed_player ADD COLUMN notes TEXT;",
            ct).ConfigureAwait(false);
    }
}

internal sealed class AddObservedPlayerLastSeenIndex : IMigration
{
    public string Id => "20260515_observed_player_last_seen_idx";

    public Task UpAsync(DbContext ctx, CancellationToken ct) =>
        ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_observed_player_last_seen " +
            "ON nexus_internal_observed_player (last_seen DESC);",
            ct);
}

internal sealed class AddRefreshQueueTable : IMigration
{
    public string Id => "20260515_refresh_queue";

    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS nexus_internal_refresh_queue (" +
            "  lodestone_id INTEGER NOT NULL," +
            "  category INTEGER NOT NULL," +
            "  priority INTEGER NOT NULL," +
            "  enqueued_at TEXT NOT NULL," +
            "  last_attempted_at TEXT NULL," +
            "  last_failed_at TEXT NULL," +
            "  attempt_count INTEGER NOT NULL DEFAULT 0," +
            "  PRIMARY KEY (lodestone_id, category));", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_refresh_queue_priority_enqueued " +
            "ON nexus_internal_refresh_queue (priority, enqueued_at);", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_refresh_queue_last_failed " +
            "ON nexus_internal_refresh_queue (last_failed_at);", ct).ConfigureAwait(false);
    }
}

internal sealed class AddPlayerHistoryIsReadColumn : IMigration
{
    public string Id => "20260517_player_history_is_read";

    /// <summary>
    /// Adds the <c>is_read</c> column to <c>nexus_internal_player_history</c> so the
    /// player-list yellow dot can hide once every row for a character has been seen
    /// in the History tab. Default <c>0</c> backfills existing rows as unread — the
    /// dot lights up immediately on upgrade, and opening each player's History tab
    /// is what flips rows to read from here on.
    /// </summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            "ALTER TABLE nexus_internal_player_history " +
            "ADD COLUMN is_read INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_player_history_content_id_is_read " +
            "ON nexus_internal_player_history (content_id, is_read);", ct).ConfigureAwait(false);
    }
}

internal sealed class AddEncountersDropSeenCount : IMigration
{
    public string Id => "20260518_encounters_and_drop_observed_aggregates";

    /// <summary>
    /// Adds the territory-bounded encounter tables (<c>nexus_internal_encounter</c>
    /// + <c>nexus_internal_player_encounter</c>) and retires three denormalized
    /// columns on <c>nexus_internal_observed_player</c> — <c>seen_count</c>,
    /// <c>first_seen</c>, <c>last_seen</c> — that are now derivable from the
    /// encounter tables:
    ///   <c>SeenCount</c>  = <c>COUNT(*)         FROM player_encounter WHERE content_id = ?</c>
    ///   <c>FirstSeen</c> = <c>MIN(first_seen_at) FROM player_encounter WHERE content_id = ?</c>
    ///   <c>LastSeen</c>  = <c>MAX(last_seen_at)  FROM player_encounter WHERE content_id = ?</c>
    /// The watcher computes them at hydrate-time via a single grouped read and
    /// keeps them on the in-memory <c>ObservedPlayer</c> record so the UI's
    /// sort / "Recent" filter / "last seen X ago" displays stay snappy without
    /// re-querying. Pre-migration values in those columns are discarded —
    /// historical counts and timestamps re-accumulate from new encounters.
    /// </summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS nexus_internal_encounter (" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  territory_type_id INTEGER NOT NULL," +
            "  started_at TEXT NOT NULL," +
            "  ended_at TEXT NULL);", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_encounter_started_at " +
            "ON nexus_internal_encounter (started_at DESC);", ct).ConfigureAwait(false);

        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS nexus_internal_player_encounter (" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT," +
            "  encounter_id INTEGER NOT NULL REFERENCES nexus_internal_encounter(id) ON DELETE CASCADE," +
            "  content_id INTEGER NOT NULL," +
            "  job_id INTEGER NOT NULL," +
            "  level INTEGER NOT NULL," +
            "  first_seen_at TEXT NOT NULL," +
            "  last_seen_at TEXT NOT NULL);", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_player_encounter_content_id " +
            "ON nexus_internal_player_encounter (content_id, first_seen_at DESC);", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_player_encounter_encounter_id " +
            "ON nexus_internal_player_encounter (encounter_id);", ct).ConfigureAwait(false);

        // Backfill: one synthetic encounter + one player_encounter per
        // existing observed_player row, so the in-memory FirstSeen / LastSeen
        // / SeenCount derivation keeps a baseline of "we saw this character
        // once at this point in time" for every migrated character. Without
        // this, the Encounters tab and the player-list sort would start from
        // an empty aggregate set on every upgrade (~14k characters reduced to
        // "—"). TerritoryTypeId is set to 0 — a sentinel for "legacy, real
        // zone unknown" — so the UI can render these rows distinctly (or
        // simply show "—" for the zone column). Pairing uses
        // ROW_NUMBER() OVER (ORDER BY content_id) on both inserts so each
        // observed_player gets a 1:1 link to its synthetic encounter; the
        // AUTOINCREMENT counters then continue from the max id for real
        // post-migration encounters.
        // Row-by-row backfill via raw ADO.NET reader instead of an INSERT...
        // SELECT with window functions: the latter consistently failed at
        // runtime on production data (SQLite exception was opaque about the
        // exact column), and the per-row path lets a single weird legacy row
        // raise a clean SqliteException with the offending values in scope.
        // Each loop iteration pairs one encounter + one player_encounter.
        // Cast to SqliteConnection so CreateCommand returns the typed
        // SqliteCommand whose Parameters collection has the
        // (string, SqliteType) overload — the abstract DbCommand path goes
        // through Parameters.Add(object) which returns an int index, not a
        // parameter handle.
        var connection = (SqliteConnection)ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct).ConfigureAwait(false);

        var observed = new List<(ulong ContentId, uint JobId, byte Level,
                                 DateTime FirstSeen, DateTime LastSeen)>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT content_id, class_job_id, level, first_seen, last_seen, updated_at " +
                "FROM nexus_internal_observed_player";
            await using var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var cid = (ulong)r.GetInt64(0);
                var job = (uint)r.GetInt32(1);
                var level = (byte)r.GetInt32(2);
                // EF stores DateTime as TEXT (ISO 8601). Some legacy rows may
                // have null timestamps; cascade to the next non-null, finally
                // to the .NET default DateTime so NOT NULL never trips.
                var firstSeen = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                var lastSeen  = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4);
                var updated   = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5);
                var fs = firstSeen ?? lastSeen ?? updated ?? default(DateTime);
                var ls = lastSeen ?? firstSeen ?? updated ?? default(DateTime);
                observed.Add((cid, job, level, fs, ls));
            }
        }

        await using (var insEnc = connection.CreateCommand())
        await using (var insPe = connection.CreateCommand())
        {
            insEnc.CommandText =
                "INSERT INTO nexus_internal_encounter " +
                "    (territory_type_id, started_at, ended_at) " +
                "VALUES (0, @started, @ended)";
            var pEncStarted = insEnc.Parameters.Add("@started", SqliteType.Text);
            var pEncEnded = insEnc.Parameters.Add("@ended", SqliteType.Text);

            insPe.CommandText =
                "INSERT INTO nexus_internal_player_encounter " +
                "    (encounter_id, content_id, job_id, level, first_seen_at, last_seen_at) " +
                "VALUES (last_insert_rowid(), @cid, @job, @level, @first, @last)";
            var pPeCid = insPe.Parameters.Add("@cid", SqliteType.Integer);
            var pPeJob = insPe.Parameters.Add("@job", SqliteType.Integer);
            var pPeLevel = insPe.Parameters.Add("@level", SqliteType.Integer);
            var pPeFirst = insPe.Parameters.Add("@first", SqliteType.Text);
            var pPeLast = insPe.Parameters.Add("@last", SqliteType.Text);

            foreach (var row in observed)
            {
                // Match Microsoft.Data.Sqlite's space-separator format so
                // these migrated rows lex-sort correctly against rows the
                // live encounter tracker writes via EF Core. ToString("O")
                // here used to plant T-format rows that compare as greater
                // than space-format rows (' ' < 'T') — invisible until any
                // SQL query orders by these columns.
                pEncStarted.Value = row.FirstSeen.ToString(SqliteDateTimeFormats.ProviderFormat, CultureInfo.InvariantCulture);
                pEncEnded.Value = row.LastSeen.ToString(SqliteDateTimeFormats.ProviderFormat, CultureInfo.InvariantCulture);
                await insEnc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                pPeCid.Value = (long)row.ContentId;
                pPeJob.Value = row.JobId;
                pPeLevel.Value = row.Level;
                pPeFirst.Value = row.FirstSeen.ToString(SqliteDateTimeFormats.ProviderFormat, CultureInfo.InvariantCulture);
                pPeLast.Value = row.LastSeen.ToString(SqliteDateTimeFormats.ProviderFormat, CultureInfo.InvariantCulture);
                await insPe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        // SQLite refuses to DROP COLUMN when the column is part of any
        // index — even though the docs claim implicit drops, in practice it
        // raises "error in DROP COLUMN: cannot drop column being used in
        // index". The 20260515_observed_player_last_seen_idx migration
        // earlier created an index on last_seen; tear that down explicitly
        // before the column drop. ALTER TABLE ... DROP COLUMN itself works
        // on SQLite 3.35+ which Dalamud's bundled engine has.
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS ix_nexus_internal_observed_player_last_seen;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "ALTER TABLE nexus_internal_observed_player DROP COLUMN seen_count;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "ALTER TABLE nexus_internal_observed_player DROP COLUMN first_seen;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "ALTER TABLE nexus_internal_observed_player DROP COLUMN last_seen;", ct).ConfigureAwait(false);
    }
}

internal sealed class RebuildRefreshQueueOnContentId : IMigration
{
    public string Id => "20260516_refresh_queue_content_id_keyed";

    /// <summary>
    /// Re-keys the queue from lodestone_id to content_id so the new
    /// <c>RefreshCategory.LodestoneId</c> task — which runs before the
    /// Lodestone id is known — has a stable identifier to write against.
    /// Queue rows are short-lived state (items are processed and deleted),
    /// so dropping the table loses no meaningful data; rows from the old
    /// schema would have been reprocessed on the next observation anyway.
    /// </summary>
    public async Task UpAsync(DbContext ctx, CancellationToken ct)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS nexus_internal_refresh_queue;", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE TABLE nexus_internal_refresh_queue (" +
            "  content_id INTEGER NOT NULL," +
            "  category INTEGER NOT NULL," +
            "  priority INTEGER NOT NULL," +
            "  enqueued_at TEXT NOT NULL," +
            "  last_attempted_at TEXT NULL," +
            "  last_failed_at TEXT NULL," +
            "  attempt_count INTEGER NOT NULL DEFAULT 0," +
            "  PRIMARY KEY (content_id, category));", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_refresh_queue_priority_category_enqueued " +
            "ON nexus_internal_refresh_queue (priority, category, enqueued_at);", ct).ConfigureAwait(false);
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_nexus_internal_refresh_queue_last_failed " +
            "ON nexus_internal_refresh_queue (last_failed_at);", ct).ConfigureAwait(false);
    }
}
