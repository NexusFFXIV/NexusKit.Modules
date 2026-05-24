using NexusKit.Modules.ExternalData.Models;

namespace NexusKit.Modules.ExternalData.FreeCompanies;

public interface IExternalDataFreeCompanyService
{
    /// <summary>
    /// Fetches a Free Company by its Lodestone id. Returns null when no enabled source
    /// can supply the entry (Lodestone disabled and nothing cached locally).
    /// </summary>
    Task<FreeCompany?> GetAsync(string lodestoneFcId, CancellationToken ct = default);

    /// <summary>
    /// DB-only lookup that returns <b>all</b> Free Companies whose <c>(tag, world_id)</c>
    /// pair matches. The pair is <i>not</i> unique — Square Enix allows arbitrarily many
    /// FCs with the same tag on the same world (e.g. four distinct «Boop!» FCs on
    /// Twintania at the time of writing). Callers MUST treat the result as a candidate
    /// list and surface ambiguity in the UI.
    /// <para>The only authoritative player↔FC link is
    /// <c>external_player_profile.free_company_lodestone_id</c>, populated from the
    /// Lodestone character-page scrape. Use this list only as a UX fallback when no
    /// profile is available yet (or when the profile has no FC id but the live
    /// observation carries a CompanyTag).</para>
    /// </summary>
    Task<IReadOnlyList<FreeCompany>> FindCandidatesByTagAndWorldAsync(
        string tag, uint worldId, CancellationToken ct = default);

    /// <summary>
    /// DB-only batch lookup keyed on Lodestone ids — never hits Lodestone, never
    /// upserts. Returns a map for every id that has a row; missing ids simply
    /// don't appear in the result. Used by the UI to resolve FC ids referenced
    /// from places like the history log into human-readable labels without
    /// triggering an enrichment side-effect.
    /// </summary>
    Task<IReadOnlyDictionary<string, FreeCompany>> GetManyCachedAsync(
        IReadOnlyCollection<string> lodestoneIds, CancellationToken ct = default);

    /// <summary>Fires after an upsert detected at least one column change on
    /// the persisted <c>nexus_external_free_company</c> row — name, tag,
    /// active member count, slogan, focus flags, etc. Carries the FC's
    /// Lodestone id; subscribers query the service if they need the full
    /// FC payload. Does not fire on first-time inserts (no prior state to
    /// diff against) nor when the upsert payload was identical to the
    /// existing row. Handlers run on the thread-pool task that performed
    /// the upsert.</summary>
    event Action<string>? FreeCompanyChanged;

    /// <summary>Fires once after an upsert that produced a brand-new row in
    /// <c>nexus_external_free_company</c> (no prior row existed). Carries the
    /// FC's Lodestone id; subscribers query the service for the full payload.
    /// Mirrors <see cref="FreeCompanyChanged"/> for the first-time case, so
    /// notification surfaces can offer a distinct "FC newly discovered" line
    /// without duplicating the change firehose. Handlers run on the
    /// thread-pool task that performed the insert.</summary>
    event Action<string>? FreeCompanyAdded;
}
