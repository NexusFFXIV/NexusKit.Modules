using NexusKit.Persistence.Maintenance;

namespace NexusKit.Modules.Lodestone.Maintenance;

/// <summary>
/// Daily eviction of expired <c>nexus_lodestone_cache</c> rows. The read
/// side (<c>LodestoneClient.TryReadCacheAsync</c>) already skips rows past
/// their <c>expires_at</c> timestamp, but never deletes them — the table
/// grows monotonically without this contributor. Six-hour cadence catches
/// the typical "let it idle overnight" pattern; aggressive enough that the
/// cache stays close to its working-set size, infrequent enough that the
/// DELETE doesn't compete with active observation traffic.
/// </summary>
internal sealed class LodestoneCacheMaintenanceContributor : ExpiredRowMaintenanceContributor
{
    public override string Name => "lodestone-cache-evict";
    public override TimeSpan Interval => TimeSpan.FromHours(6);
    protected override string TableName => "nexus_lodestone_cache";
}
