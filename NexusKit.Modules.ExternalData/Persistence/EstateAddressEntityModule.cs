using Microsoft.EntityFrameworkCore;
using NexusKit.Persistence.Schema;

namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// EF Core configuration for the <c>nexus_estate_address</c> table.
/// Separate <see cref="IEntityModule"/> rather than added to
/// <see cref="ExternalDataEntityModule"/> so the table can stand alone — future
/// owner entities (player) will reference it from their own modules.
/// </summary>
internal sealed class EstateAddressEntityModule : IEntityModule
{
    public string TablePrefix => "estate";

    public void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EstateAddressEntity>(e =>
        {
            e.ToTable("address");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
            e.Property(x => x.Greeting).HasColumnName("greeting");
            e.Property(x => x.DataCenterId).HasColumnName("data_center_id");
            e.Property(x => x.WorldId).HasColumnName("world_id");
            e.Property(x => x.DistrictTerritoryId).HasColumnName("district_territory_id");
            e.Property(x => x.Ward).HasColumnName("ward");
            e.Property(x => x.PlotNumber).HasColumnName("plot_number");
            e.Property(x => x.IsApartment).HasColumnName("is_apartment");
            e.Property(x => x.ApartmentWing).HasColumnName("apartment_wing");
            e.Property(x => x.HouseSize).HasColumnName("house_size");
            e.Property(x => x.IsSubdivision).HasColumnName("is_subdivision");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            // Lookup-helper for navigation / DbInspect "find existing address by
            // physical location" — not unique because Lodestone occasionally
            // returns matching strings for different rows during transitional
            // states (FC dissolved, plot resold).
            e.HasIndex(x => new { x.WorldId, x.DistrictTerritoryId, x.Ward, x.PlotNumber })
                .HasDatabaseName("ix_nexus_estate_address_world_district_ward_plot");
        });
    }
}
