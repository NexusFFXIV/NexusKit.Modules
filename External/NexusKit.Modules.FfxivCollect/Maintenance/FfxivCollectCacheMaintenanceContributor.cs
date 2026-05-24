using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.FfxivCollect.Maintenance;

/// <summary>
/// Daily eviction of expired <c>nexus_ffxivcollect_cache</c> rows. Same
/// rationale as the Lodestone cache contributor — the read side already
/// honours the row's <c>expires_at</c>, but never deletes; without
/// periodic eviction the table accumulates forever.
/// </summary>
internal sealed class FfxivCollectCacheMaintenanceContributor : ExpiredRowMaintenanceContributor
{
    public override string Name => "ffxivcollect-cache-evict";
    public override TimeSpan Interval => TimeSpan.FromHours(6);
    protected override string TableName => "nexus_ffxivcollect_cache";
}
