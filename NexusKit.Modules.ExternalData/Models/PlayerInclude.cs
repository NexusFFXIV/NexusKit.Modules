namespace NexusKit.Modules.ExternalData.Models;

/// <summary>
/// Optional sub-resources the caller wants included alongside a player's core identity.
/// Flags are additive; the player core (name/world/datacenter) is always returned when available.
/// </summary>
[Flags]
public enum PlayerInclude
{
    None         = 0,
    Profile      = 1 << 0,
    Mounts       = 1 << 1,
    Minions      = 1 << 2,
    Achievements = 1 << 3,
    Items        = 1 << 4,
    ClassJobs    = 1 << 5,
    FreeCompany  = 1 << 6,
    Gear         = 1 << 7,
    All = Profile | Mounts | Minions | Achievements | Items | ClassJobs | FreeCompany | Gear,
}
