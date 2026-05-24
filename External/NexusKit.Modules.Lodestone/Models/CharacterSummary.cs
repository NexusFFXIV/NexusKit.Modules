namespace NexusKit.Modules.Lodestone.Models;

/// <summary>
/// Lightweight projection of a Lodestone character page. We map NetStone's parsed
/// model into this POCO so the cache is plain JSON (NetStone's types include Uri
/// and similar that don't round-trip cleanly) and consumers get a stable contract
/// independent of NetStone's internal class layout.
/// </summary>
public sealed class CharacterSummary
{
    public ulong LodestoneId { get; set; }

    /// <summary>Character name as shown on the Lodestone page header. Populated
    /// from NetStone's <c>LodestoneCharacter.Name</c>. Used by the External
    /// data service as a fallback identity source when FFXIVCollect's character
    /// endpoint is unavailable — the Lodestone page is enough on its own to
    /// resolve a player's name, home world, and (via Lumina) data center.</summary>
    public string? Name { get; set; }

    /// <summary>Home-world name as parsed from the Lodestone page (e.g.
    /// "Phoenix"). Language-invariant brand strings, so the External data
    /// service maps this through Lumina's English World sheet to obtain the
    /// world id.</summary>
    public string? Server { get; set; }

    public string? Bio { get; set; }
    public string? Nameday { get; set; }
    public string? GuardianDeityName { get; set; }
    public string? GuardianDeityIconUrl { get; set; }
    public string? StartingCityName { get; set; }
    public string? StartingCityIconUrl { get; set; }
    public string? AvatarUrl { get; set; }
    public string? PortraitUrl { get; set; }
    public string? FreeCompanyLodestoneId { get; set; }

    /// <summary>Inline FC name as it appears on the character page header
    /// (e.g. "Azem's Legacy"). Populated by the direct HTML scraper —
    /// NetStone exposes the FC link but not the human-readable name.
    /// Useful for showing FC affiliation without an extra round-trip to the
    /// FC's own Lodestone page.</summary>
    public string? FreeCompanyName { get; set; }

    /// <summary>Race name as parsed from the character-header block (e.g.
    /// "Lalafell"). Mirrors what <c>ObservedPlayer.Customize[0]</c> resolves
    /// to via Lumina; useful for the un-observed cohort.</summary>
    public string? Race { get; set; }

    /// <summary>Tribe / clan within the race (e.g. "Plainsfolk" under
    /// Lalafell).</summary>
    public string? Tribe { get; set; }

    /// <summary>Gender as rendered on the character page — either the symbol
    /// glyph ("♂" / "♀") or the localized word, depending on Lodestone's
    /// HTML. The scraper normalises to the symbol form when possible so
    /// consumers can do a simple equality compare.</summary>
    public string? Gender { get; set; }

    /// <summary>Level of the class the character is currently presenting.
    /// The class name itself isn't on the character page as parsable text —
    /// only an icon + JS tooltip — so we don't surface a matching
    /// <c>ActiveClassJobName</c>. Consumers that need the name use
    /// <c>ObservedPlayer.ClassJobId</c> (in-game live data, resolved via
    /// Lumina) or the full <c>GetCharacterClassJobsAsync</c> breakdown.</summary>
    public int? ActiveClassJobLevel { get; set; }

    public string? PvpTeamLodestoneId { get; set; }
    public string? PvpTeamName { get; set; }

    /// <summary>Player's personal Grand Company allegiance — distinct from the FC's
    /// own Grand Company (which lives on <c>LodestoneFreeCompany</c>).</summary>
    public string? GrandCompanyName { get; set; }

    /// <summary>Rank title within the player's Grand Company (e.g. "Enlightened
    /// Serpent Priestess" / "Erleuchtete Schlangenpriesterin").</summary>
    public string? GrandCompanyRank { get; set; }
}
