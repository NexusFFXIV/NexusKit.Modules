namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// One class/job for a player and its current level. <see cref="ClassJobId"/> is
/// the Lumina <c>ClassJob.RowId</c>; resolve to a localized name via
/// <c>IGameDataLookups.GetClassJobName</c>.
/// </summary>
public sealed record PlayerClassJob(uint ClassJobId, int Level);
