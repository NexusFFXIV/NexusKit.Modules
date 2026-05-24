using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.FfxivCollect.Persistence;

internal sealed class FfxivCollectCacheEntityModule : IEntityModule
{
    public string TablePrefix => "ffxivcollect";

    public void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FfxivCollectCacheEntity>(e =>
        {
            e.ToTable("cache");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(512);
            e.Property(x => x.Response).HasColumnName("response").IsRequired();
            e.Property(x => x.FetchedAt).HasColumnName("fetched_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        });
    }
}
