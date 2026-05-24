using NexusKit.Core;

namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Full estate metadata stored in <c>nexus_estate_address</c>: identifying
/// address fields (world / district / ward / plot) plus owner-given metadata
/// (name, greeting) and the locale-independent <see cref="HouseSize"/> enum.
/// <para>For FC estates <see cref="IsApartment"/> is always <c>false</c> and
/// <see cref="ApartmentWing"/> is <c>null</c> — those columns exist for future
/// player-apartment rows.</para>
/// </summary>
public sealed record EstateAddress(
    long Id,
    string? Name,
    string? Greeting,
    uint? DataCenterId,
    uint? WorldId,
    uint? DistrictTerritoryId,
    int? Ward,
    int? PlotNumber,
    bool IsApartment,
    int? ApartmentWing,
    HouseSize? HouseSize,
    bool IsSubdivision) : IHousingAddress;
