using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.Lodestone.Persistence;

internal sealed class LodestoneCacheEntityModule : IEntityModule
{
    public string TablePrefix => "lodestone";

    public void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LodestoneCacheEntity>(e =>
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
