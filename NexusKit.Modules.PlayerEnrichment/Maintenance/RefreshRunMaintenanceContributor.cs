using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

internal sealed class RefreshRunMaintenanceContributor : IDbMaintenanceContributor
{
    private const int KeepRuns = 20;
    private readonly ILogger<RefreshRunMaintenanceContributor> mLog;

    public RefreshRunMaintenanceContributor(ILogger<RefreshRunMaintenanceContributor> log)
        => mLog = log;

    public string Name => "refresh-run-retention";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(DbContext ctx, CancellationToken ct)
    {
        var old = await ctx.Set<InternalRefreshRunEntity>()
            .OrderByDescending(x => x.StartedAt)
            .Skip(KeepRuns)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0) return;

        var ids = old.Select(x => x.Id).ToArray();
        await ctx.Set<InternalRefreshRunItemEntity>()
            .Where(x => ids.Contains(x.RunId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        ctx.Set<InternalRefreshRunEntity>().RemoveRange(old);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        mLog.LogInformation("Refresh run retention: removed {Count} old run(s).", old.Count);
    }
}
