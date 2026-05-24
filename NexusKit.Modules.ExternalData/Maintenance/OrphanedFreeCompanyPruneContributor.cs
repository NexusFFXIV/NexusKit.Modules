using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.ExternalData.Persistence;
using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.ExternalData.Maintenance;

/// <summary>
/// Periodic eviction of orphaned <c>nexus_external_free_company</c> rows — FCs
/// that no <c>nexus_external_player_profile.free_company_lodestone_id</c>
/// currently points at. These accumulate naturally as observed players switch
/// FCs over time: their profile rewrites the FK to the new FC, leaving the
/// previous FC row unreachable from the per-player refresh queue (which is
/// keyed on <c>content_id</c>) and frozen against the live Lodestone.
///
/// <para>Quarterly cadence — orphan rows aren't urgent; even a 90-day-old
/// stale row is fine until the sweep collects it. The interval comfortably
/// outruns the typical FC-hop pattern so we don't bin rows the user is
/// actively curating.</para>
/// </summary>
internal sealed class OrphanedFreeCompanyPruneContributor : IDbMaintenanceContributor
{
    private readonly ILogger<OrphanedFreeCompanyPruneContributor> mLog;

    public OrphanedFreeCompanyPruneContributor(ILogger<OrphanedFreeCompanyPruneContributor> log)
    {
        mLog = log;
    }

    public string Name => "external-free-company-orphan-prune";

    public TimeSpan Interval => TimeSpan.FromDays(90);

    public async Task RunAsync(DbContext ctx, CancellationToken ct)
    {
        var deleted = await ctx.Set<FreeCompanyEntity>()
            .Where(fc => !ctx.Set<PlayerProfileEntity>()
                .Any(pp => pp.FreeCompanyLodestoneId == fc.LodestoneId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            mLog.LogInformation(
                "ExternalData: pruned {Count} orphaned free_company row(s) with no observed-player link.",
                deleted);
    }
}
