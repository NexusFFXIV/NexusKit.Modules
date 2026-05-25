using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Persistence;

namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

internal sealed class RefreshQueueStatsService : IRefreshQueueStatsService
{
    private const int TopContentLimit = 20;

    // EF Core's Microsoft.Data.Sqlite provider serialises DateTime columns
    // as text with this exact pattern (space separator, 7 fractional digits,
    // no T/Z). Comparison against parameters has to use the same shape or
    // SQLite falls back to lexicographic compare and silently mis-orders rows.
    private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private readonly INexusDbContextFactory mFactory;
    private readonly ILogger<RefreshQueueStatsService> mLog;

    public RefreshQueueStatsService(
        INexusDbContextFactory factory,
        ILogger<RefreshQueueStatsService> log)
    {
        mFactory = factory;
        mLog = log;
    }

    public async Task<RefreshQueueStatsSnapshot> GatherAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ctx = await mFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var connection = ctx.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(ct).ConfigureAwait(false);

            // Totals + exhausted split + earliest cooldown row: one CTE-less
            // query, four columns, single full-table scan. Sub-10 ms on a
            // 70k-row table.
            var total = 0;
            var exhausted = 0;
            var eligibleNow = 0;
            string? minWaitingFailedAt = null;
            var cutoff = DateTime.UtcNow - PlayerRefreshQueueService.FailureBackoff;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        COUNT(*),
                        SUM(CASE WHEN attempt_count >= @maxAttempts THEN 1 ELSE 0 END),
                        SUM(CASE WHEN attempt_count < @maxAttempts
                                  AND (priority = 0
                                       OR last_failed_at IS NULL
                                       OR last_failed_at < @cutoff)
                                 THEN 1 ELSE 0 END),
                        MIN(CASE WHEN attempt_count < @maxAttempts
                                  AND priority <> 0
                                  AND last_failed_at IS NOT NULL
                                  AND last_failed_at >= @cutoff
                                 THEN last_failed_at END)
                    FROM nexus_internal_refresh_queue";
                AddParam(cmd, "@maxAttempts", PlayerRefreshQueueService.MaxAttempts);
                // Format @cutoff to match how EF Core's SQLite provider stores
                // DateTime ("yyyy-MM-dd HH:mm:ss.fffffff", space separator, no
                // T/Z). Without this the lexicographic compare in SQLite places
                // a space-formatted DB value before a "O"-formatted parameter
                // (' ' < 'T') and every fresh failure looks "older than the
                // cutoff" — which silently shovelled cooldown rows into
                // eligibleNow and broke the new status-line split.
                AddParam(cmd, "@cutoff",
                    cutoff.ToString(SqliteDateTimeFormat, CultureInfo.InvariantCulture));
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    total = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    exhausted = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    eligibleNow = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    minWaitingFailedAt = r.IsDBNull(3) ? null : r.GetString(3);
                }
            }
            var active = total - exhausted;
            var backoffWaiting = active - eligibleNow;

            // Two ETAs, two questions:
            //  - Worker ETA = drain time for the rows the worker can pick
            //    *right now*: WorkerGap × EligibleNow. Goes to zero the
            //    moment the queue is "done as far as the worker can go".
            //  - EarliestRetryAt = when the next backoff-waiting row clears
            //    its cooldown. Null when nothing is waiting. UI computes the
            //    relative countdown at render time so it ticks without a
            //    Refresh click.
            // Without this split, an idle worker with 60+ rows in cooldown
            // shows a steady 1-2 minute "ETA" and looks hung.
            var estimatedDrain = TimeSpan.FromTicks(
                PlayerRefreshQueueService.WorkerGap.Ticks * eligibleNow);
            var earliestRetryAt = ParseSqliteDateTimeUtc(minWaitingFailedAt) is { } parsedRetry
                ? parsedRetry + PlayerRefreshQueueService.FailureBackoff
                : (DateTime?)null;

            var byPriority = await GroupAsync(connection,
                "SELECT priority, COUNT(*) FROM nexus_internal_refresh_queue GROUP BY priority",
                ct).ConfigureAwait(false);
            var byCategory = await GroupAsync(connection,
                "SELECT category, COUNT(*) FROM nexus_internal_refresh_queue GROUP BY category",
                ct).ConfigureAwait(false);

            var top = new List<RefreshQueueTopContent>(TopContentLimit);
            await using (var cmd = connection.CreateCommand())
            {
                // Two MIN(CASE...) aggregates surface the per-row schedule the
                // Settings UI needs: the earliest cooldown-clearance for
                // *non-exhausted* rows of this content (drives "Retry in …"),
                // and the earliest attempt-stamp of *exhausted* rows (drives
                // "Cleanup in …" via the maintenance contributor's 24h TTL).
                // Both are NULL when the corresponding subset is empty —
                // populated as DateTime? on the DTO so the UI can fall back
                // to "Retry pending" / "Cleanup pending" placeholders.
                cmd.CommandText = $@"
                    SELECT q.content_id, o.name, COUNT(*) AS rows,
                           MAX(q.attempt_count) AS max_attempts,
                           MIN(CASE WHEN q.attempt_count <  @maxAttempts AND q.last_failed_at    IS NOT NULL
                                    THEN q.last_failed_at    END) AS earliest_failed,
                           MIN(CASE WHEN q.attempt_count >= @maxAttempts AND q.last_attempted_at IS NOT NULL
                                    THEN q.last_attempted_at END) AS earliest_attempted
                    FROM nexus_internal_refresh_queue q
                    LEFT JOIN nexus_internal_observed_player o
                        ON o.content_id = q.content_id
                    GROUP BY q.content_id
                    ORDER BY rows DESC
                    LIMIT {TopContentLimit}";
                AddParam(cmd, "@maxAttempts", PlayerRefreshQueueService.MaxAttempts);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var earliestFailed = r.IsDBNull(4) ? null : r.GetString(4);
                    var earliestAttempted = r.IsDBNull(5) ? null : r.GetString(5);
                    var nextRetryAt = ParseSqliteDateTimeUtc(earliestFailed) is { } f
                        ? f + PlayerRefreshQueueService.FailureBackoff
                        : (DateTime?)null;
                    var deletionAt = ParseSqliteDateTimeUtc(earliestAttempted) is { } a
                        ? a + RefreshQueueMaintenanceContributor.ExhaustedRetention
                        : (DateTime?)null;
                    top.Add(new RefreshQueueTopContent(
                        ContentId: (ulong)r.GetInt64(0),
                        Name: r.IsDBNull(1) ? null : r.GetString(1),
                        Rows: r.GetInt32(2),
                        MaxAttemptCount: r.GetInt32(3),
                        EarliestNextRetryAtUtc: nextRetryAt,
                        EarliestDeletionAtUtc: deletionAt));
                }
            }

            return new RefreshQueueStatsSnapshot(
                TotalRows: total,
                ActiveRows: active,
                ExhaustedRows: exhausted,
                EligibleNowRows: eligibleNow,
                BackoffWaitingRows: backoffWaiting,
                EstimatedDrain: estimatedDrain,
                EarliestRetryAt: earliestRetryAt,
                RowsByPriority: byPriority,
                RowsByCategory: byCategory,
                TopContents: top);
        }
        catch (OperationCanceledException)
        {
            return Empty;
        }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "RefreshQueueStats: gather failed");
            return Empty;
        }
    }

    private static readonly RefreshQueueStatsSnapshot Empty = new(
        TotalRows: 0,
        ActiveRows: 0,
        ExhaustedRows: 0,
        EligibleNowRows: 0,
        BackoffWaitingRows: 0,
        EstimatedDrain: TimeSpan.Zero,
        EarliestRetryAt: null,
        RowsByPriority: new Dictionary<int, int>(),
        RowsByCategory: new Dictionary<int, int>(),
        TopContents: Array.Empty<RefreshQueueTopContent>());

    private static async Task<IReadOnlyDictionary<int, int>> GroupAsync(
        System.Data.Common.DbConnection connection, string sql, CancellationToken ct)
    {
        var result = new Dictionary<int, int>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            result[r.GetInt32(0)] = r.GetInt32(1);
        return result;
    }

    /// <summary>Parses a SQLite TEXT timestamp written by EF Core's provider
    /// (space-separator, 7 fractional digits, no T/Z) into a UTC
    /// <see cref="DateTime"/>. Returns null for null or unparseable input —
    /// callers treat that as "no scheduled time" rather than throwing.</summary>
    private static DateTime? ParseSqliteDateTimeUtc(string? value)
    {
        if (value is null) return null;
        if (!DateTime.TryParseExact(value,
                SqliteDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            return null;
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
