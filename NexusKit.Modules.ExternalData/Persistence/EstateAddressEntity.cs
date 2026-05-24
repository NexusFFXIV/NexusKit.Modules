using NexusKit.Core;

namespace NexusKit.Modules.ExternalData.Persistence;

/// <summary>
/// Estate address row. Auto-increment <see cref="Id"/> is the FK target for
/// <see cref="FreeCompanyEntity.EstateAddressId"/> (and, in future versions, a
/// matching column on the player entity for owned houses / apartments).
/// </summary>
public sealed class EstateAddressEntity
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Greeting { get; set; }
    public uint? DataCenterId { get; set; }
    public uint? WorldId { get; set; }
    public uint? DistrictTerritoryId { get; set; }
    public int? Ward { get; set; }
    public int? PlotNumber { get; set; }
    public bool IsApartment { get; set; }
    public int? ApartmentWing { get; set; }
    public HouseSize? HouseSize { get; set; }
    public bool IsSubdivision { get; set; }
    public DateTime UpdatedAt { get; set; }
}
