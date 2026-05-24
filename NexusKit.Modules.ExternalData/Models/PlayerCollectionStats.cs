namespace NexusKit.Modules.ExternalData.Models;

public sealed record PlayerCollectionStats(
    int Count,
    int Total,
    int? Ranking = null,
    bool? IsPublic = null);
