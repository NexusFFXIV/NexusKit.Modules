namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Minimal core-identity tuple of a previously-fetched player. Returned by
/// DB-only lookups (e.g. <c>IExternalDataPlayerService.GetByNameAsync</c>)
/// where the caller only needs to know "do we have this character" without
/// paying the deserialization cost of the full <see cref="Player"/> graph.
/// </summary>
public sealed record PlayerIdentity(
    ulong LodestoneId,
    string Name,
    uint HomeWorldId,
    uint DataCenterId);
