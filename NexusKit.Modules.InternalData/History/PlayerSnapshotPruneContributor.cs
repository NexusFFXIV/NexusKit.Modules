using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.InternalData.History;

/// <summary>Retains all recent snapshots and one month-end representative for
/// older data. Snapshot history is comparison state; detailed changes remain in
/// the separate player-history table.</summary>
internal sealed class PlayerSnapshotPruneContributor : IDbMaintenanceContributor
{
    private readonly ILogger<PlayerSnapshotPruneContributor> mLog;

    public PlayerSnapshotPruneContributor(ILogger<PlayerSnapshotPruneContributor> log) => mLog = log;

    public string Name => "player-snapshot-prune";
    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task RunAsync(DbContext ctx, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        const string sql = """
            DELETE FROM nexus_internal_player_snapshot
            WHERE captured_at < @cutoff
              AND id NOT IN (
                SELECT MAX(id)
                FROM nexus_internal_player_snapshot
                WHERE captured_at < @cutoff
                GROUP BY content_id, strftime('%Y-%m', captured_at)
              )
              AND id NOT IN (
                SELECT MAX(id)
                FROM nexus_internal_player_snapshot
                GROUP BY content_id
              );
            """;
        var deleted = await ctx.Database.ExecuteSqlRawAsync(sql,
            new[] { new SqliteParameter("@cutoff", cutoff) }, ct).ConfigureAwait(false);
        if (deleted > 0)
            mLog.LogInformation("Player snapshot prune removed {Count} compacted snapshot(s).", deleted);
    }
}
