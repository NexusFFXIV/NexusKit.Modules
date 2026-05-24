using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetStone.Search.Character;
using NexusKit.Core;
using NexusKit.Core.Localization;
using NexusKit.Modules.Lodestone.Models;
using NexusKit.Modules.Lodestone.Persistence;
using NexusKit.Persistence;
using NexusKit.Persistence.Settings;

namespace NexusKit.Modules.Lodestone.Clients;

internal sealed class LodestoneClient : ILodestoneClient
{
    private readonly ISettingsStore mSettingsStore;
    private readonly INexusDbContextFactory mDbFactory;
    private readonly LocalizationManager? mLocalization;
    private readonly ILogger<LodestoneClient> mLog;

    private NetStone.LodestoneClient? mNetClient;
    private string? mNetClientBaseAddress;
    private readonly SemaphoreSlim mNetClientLock = new(1, 1);

    public LodestoneClient(
        ISettingsStore settingsStore,
        INexusDbContextFactory dbFactory,
        ILogger<LodestoneClient> log,
        LocalizationManager? localization = null)
    {
        mSettingsStore = settingsStore;
        mDbFactory = dbFactory;
        mLog = log;
        mLocalization = localization;
        // One-shot session-startup cleanup of expired rows. Read-side
        // TryReadCacheAsync already skips expired entries, but never deletes
        // them — without this sweep the table grew unbounded (21+ MB of mostly
        // stale HTML scrapes in the wild). Fire-and-forget on the threadpool so
        // DI construction stays cheap; the DELETE is a single indexed scan.
        _ = Task.Run(PruneExpiredCacheAsync);
    }

    private async Task PruneExpiredCacheAsync()
    {
        if (mDbFactory.IsStopping) return;
        // CreateDbContextAsync without a ct uses the factory's LifetimeToken
        // automatically. Subsequent EF operations (ExecuteDeleteAsync etc.) do
        // NOT inherit it — the DbContext itself doesn't carry the token — so
        // we still pass it explicitly there.
        var ct = mDbFactory.LifetimeToken;
        try
        {
            await using var ctx = await mDbFactory.CreateDbContextAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var deleted = await ctx.Set<LodestoneCacheEntity>()
                .Where(e => e.ExpiresAt < now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                mLog.LogInformation("Lodestone cache: pruned {Count} expired row(s) at session start.", deleted);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogDebug(ex, "Lodestone cache: expired-row prune failed at session start.");
        }
    }

    private CultureInfo CurrentCulture
        => mLocalization?.CurrentCulture ?? CultureInfo.CurrentUICulture;

    private string LangTag => LodestoneRegion.CacheTag(CurrentCulture);

    private string LodestoneBaseAddress => LodestoneRegion.BaseAddressFor(CurrentCulture);

    public async Task<CharacterSummary?> GetCharacterAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:char:{lodestoneId}";
        var cached = await TryReadCacheAsync<CharacterSummary>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        NetStone.Model.Parseables.Character.LodestoneCharacter? ch;
        try
        {
            ch = await client.GetCharacter(lodestoneId.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone GetCharacter failed for {Id}", lodestoneId);
            return null;
        }
        if (ch is null)
        {
            mLog.LogInformation("Lodestone GetCharacter returned null for {Id} (404 or parse failure)", lodestoneId);
            return null;
        }

        var summary = new CharacterSummary
        {
            LodestoneId = lodestoneId,
            Name = TryReadString(ch, "Name"),
            Server = TryReadString(ch, "Server") ?? TryReadString(ch, "World"),
            Bio = ch.Bio,
            Nameday = ch.Nameday,
            GuardianDeityName = ch.GuardianDeityName,
            GuardianDeityIconUrl = ch.GuardianDeityIcon?.ToString(),
            StartingCityName = ch.TownName,
            StartingCityIconUrl = ch.TownIcon?.ToString(),
            AvatarUrl = ch.Avatar?.ToString(),
            PortraitUrl = ch.Portrait?.ToString(),
            FreeCompanyLodestoneId = TryReadSocialId(ch, "FreeCompany"),
            FreeCompanyName = TryReadString(ch, "FreeCompanyName") ?? TryReadSocialName(ch, "FreeCompany"),
            Race = TryReadString(ch, "Race") ?? TryReadString(ch, "RaceName"),
            Tribe = TryReadString(ch, "Tribe") ?? TryReadString(ch, "Clan"),
            Gender = NormaliseGender(ch),
            ActiveClassJobLevel = TryReadInt(ch, "ActiveClassJobLevel"),
            PvpTeamLodestoneId = TryReadSocialId(ch, "PvPTeam"),
            PvpTeamName = TryReadSocialName(ch, "PvPTeam"),
            GrandCompanyName = string.IsNullOrWhiteSpace(ch.GrandCompanyName) ? null : ch.GrandCompanyName.Trim(),
            GrandCompanyRank = string.IsNullOrWhiteSpace(ch.GrandCompanyRank) ? null : ch.GrandCompanyRank.Trim(),
        };

        // Cache-poison guard: a parsed-but-degenerate summary (transient
        // Lodestone error page, rate-limit response, scraper drift) leaves
        // Name or Server empty. Writing that to cache sticks for the full
        // 24h TTL and breaks ExternalDataPlayerService.GetAsync's
        // identity-resolution waterfall (which bails to ReadFromDbAsync the
        // moment name/server are empty), which in turn loops the refresh
        // queue through MarkFailedAsync on every retry. Returning the value
        // without caching lets the queue's MaxAttempts=5 budget run its
        // course and exhausts cleanly instead of getting stuck for a day.
        if (!string.IsNullOrEmpty(summary.Name) && !string.IsNullOrEmpty(summary.Server))
        {
            await WriteCacheAsync(cacheKey, summary, settings, ct).ConfigureAwait(false);
        }
        else
        {
            mLog.LogWarning("Lodestone GetCharacter parsed empty Name/Server for {Id} — skipping cache write so the refresh queue can retry.", lodestoneId);
        }
        return summary;
    }

    public async Task<CharacterSearchResult?> SearchCharacterAsync(string name, string world, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var cacheKey = $"lodestone:{LangTag}:search:{name.ToLowerInvariant()}|{world.ToLowerInvariant()}";
        var cached = await TryReadCacheAsync<CharacterSearchResult>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        var query = new CharacterSearchQuery
        {
            CharacterName = name,
            World = world,
        };

        NetStone.Model.Parseables.Search.Character.CharacterSearchPage? page;
        try
        {
            page = await client.SearchCharacter(query).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone SearchCharacter failed for {Name}@{World}", name, world);
            return null;
        }
        if (page is null) return null;

        var result = new CharacterSearchResult
        {
            Results = page.Results.Select(r => new CharacterSearchEntry
            {
                LodestoneId = ulong.TryParse(r.Id, out var id) ? id : 0,
                Name = r.Name ?? string.Empty,
                Server = TryReadString(r, "Server") ?? TryReadString(r, "World"),
            }).ToArray(),
        };

        // Empty Results is ambiguous between "no character with that
        // name@world exists on Lodestone yet" and "Lodestone search HTML
        // drifted / rate-limited and we parsed zero hits". Either way,
        // caching empty pins ProcessLodestoneIdAsync into a fast
        // MarkFailedAsync loop until the 24h TTL expires — exactly the
        // legacy-char-cache failure mode the queue is supposed to back off
        // from. Skip the write so each retry within the queue's
        // MaxAttempts=5 budget actually hits live (worst case 4 extra
        // Lodestone calls per unresolved player over the 5h backoff window,
        // then ExhaustedAttempts and no more traffic until the user hits
        // Refresh).
        if (result.Results.Count > 0)
        {
            await WriteCacheAsync(cacheKey, result, settings, ct).ConfigureAwait(false);
        }
        else
        {
            mLog.LogInformation(
                "Lodestone SearchCharacter returned empty for {Name}@{World} — skipping cache write so the refresh queue can retry live.",
                name, world);
        }
        return result;
    }

    public async Task<IReadOnlyList<LodestoneClassJob>?> GetCharacterClassJobsAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:classjobs:{lodestoneId}";
        var cached = await TryReadCacheAsync<List<LodestoneClassJob>>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        object? cj;
        try
        {
            // Method exists on NetStone's LodestoneClient but its return type sits in a
            // namespace we don't otherwise reference — fetch via reflection so we stay
            // independent of NetStone's exact parsed-type layout.
            var method = client.GetType().GetMethod("GetCharacterClassJob",
                new[] { typeof(string) });
            if (method is null) return null;
            var task = (Task?)method.Invoke(client, new object[] { lodestoneId.ToString() });
            if (task is null) return null;
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            cj = resultProp?.GetValue(task);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone GetCharacterClassJob failed for {Id}", lodestoneId);
            return null;
        }
        if (cj is null) return null;

        var results = new List<LodestoneClassJob>();
        foreach (var prop in cj.GetType().GetProperties())
        {
            var value = prop.GetValue(cj);
            if (value is null) continue;
            var levelProp = value.GetType().GetProperty("Level");
            if (levelProp is null) continue;
            object? levelVal;
            try
            {
                // NetStone's Level getter parses the scraped text eagerly and throws
                // FormatException for unlocked-but-unleveled jobs (empty level text).
                // Skip those rather than letting the whole ClassJobs fetch crash.
                levelVal = levelProp.GetValue(value);
            }
            catch (TargetInvocationException) { continue; }
            catch (FormatException) { continue; }
            if (levelVal is null) continue;
            if (!int.TryParse(levelVal.ToString(), out var level) || level <= 0) continue;
            results.Add(new LodestoneClassJob { Name = prop.Name, Level = level });
        }

        await WriteListCacheIfNonEmptyAsync(cacheKey, results, settings, "classjobs", lodestoneId, ct).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<LodestoneAchievementEntry>?> GetCharacterAchievementsAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:achievements:{lodestoneId}";
        var cached = await TryReadCacheAsync<List<LodestoneAchievementEntry>>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        var method = client.GetType().GetMethod("GetCharacterAchievement",
            new[] { typeof(string), typeof(int) });
        if (method is null) return null;

        var results = new List<LodestoneAchievementEntry>();
        var seen = new HashSet<uint>();
        const int maxPages = 200;

        // NetStone uses linked-list pagination: each page exposes a GetNextPage() that
        // returns null when there are no more pages. Start with page 1 via the explicit
        // method call, then follow the chain.
        object? page;
        try
        {
            var task = (Task?)method.Invoke(client, new object[] { lodestoneId.ToString(), 1 });
            if (task is null) return results; // reflection drift — let the queue retry & exhaust
            await task.ConfigureAwait(false);
            page = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone GetCharacterAchievement failed for {Id} page 1", lodestoneId);
            return null;
        }

        var visited = 0;
        while (page is not null && visited < maxPages)
        {
            ct.ThrowIfCancellationRequested();
            visited++;
            var added = ExtractAchievementsFromPage(page, results, seen);
            if (!added) break;

            try
            {
                page = await InvokeGetNextPageAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mLog.LogWarning(ex, "Lodestone GetNextPage (achievements) failed for {Id} after page {N}", lodestoneId, visited);
                break;
            }
        }

        await WriteListCacheIfNonEmptyAsync(cacheKey, results, settings, "achievements", lodestoneId, ct).ConfigureAwait(false);
        return results;
    }

    private static async Task<object?> InvokeGetNextPageAsync(object page)
    {
        var method = page.GetType().GetMethod("GetNextPage", Type.EmptyTypes);
        if (method is null) return null;
        var result = method.Invoke(page, null);
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        return result;
    }

    private static bool ExtractAchievementsFromPage(object page, List<LodestoneAchievementEntry> sink, HashSet<uint> seen)
    {
        var achievementsProp = page.GetType().GetProperty("Achievements");
        if (achievementsProp?.GetValue(page) is not System.Collections.IEnumerable items) return false;

        var added = false;
        foreach (var item in items)
        {
            if (item is null) continue;
            var t = item.GetType();
            var idVal = t.GetProperty("Id")?.GetValue(item);
            if (idVal is null) continue;
            uint achId;
            try { achId = Convert.ToUInt32(idVal); } catch { continue; }
            if (!seen.Add(achId)) continue;

            var nameVal = t.GetProperty("Name")?.GetValue(item) as string;
            var timeVal = t.GetProperty("TimeAchieved")?.GetValue(item);

            sink.Add(new LodestoneAchievementEntry
            {
                Id = achId,
                Name = nameVal ?? string.Empty,
                AchievedAt = timeVal is DateTime dt ? dt : null,
            });
            added = true;
        }
        return added;
    }

    public async Task<LodestoneFreeCompany?> GetFreeCompanyAsync(string freeCompanyLodestoneId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(freeCompanyLodestoneId)) return null;

        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:fc:{freeCompanyLodestoneId}";
        var cached = await TryReadCacheAsync<LodestoneFreeCompany>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        object? fc;
        try
        {
            var method = client.GetType().GetMethod("GetFreeCompany", new[] { typeof(string) });
            if (method is null) return null;
            var task = (Task?)method.Invoke(client, new object[] { freeCompanyLodestoneId });
            if (task is null) return null;
            await task.ConfigureAwait(false);
            fc = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone GetFreeCompany failed for {Id}", freeCompanyLodestoneId);
            return null;
        }
        if (fc is null) return null;

        var result = new LodestoneFreeCompany
        {
            LodestoneId = freeCompanyLodestoneId,
            Name = TryReadString(fc, "Name") ?? string.Empty,
            Tag = NormalizeTag(TryReadString(fc, "Tag")),
            Slogan = TryReadString(fc, "Slogan"),
            FormedAt = TryReadDateTime(fc, "Formed"),
            World = TryReadString(fc, "World"),
            Rank = TryReadInt(fc, "Rank"),
            ActiveMemberCount = TryReadInt(fc, "ActiveMemberCount"),
            ActiveState = TryReadActiveState(fc),
            RecruitmentOpen = TryReadRecruitmentOpen(fc),
            GrandCompany = TryReadString(fc, "GrandCompany"),
            Estate = TryReadEstate(fc),
            Focus = TryReadFocus(fc),
        };

        // Same poison-cache guard as GetCharacterAsync: a degenerate parse with
        // an empty Name leaves the FC upsert path with nothing to persist;
        // caching it would block re-fetch for the full TTL. Caller still gets
        // the (empty-named) value back so the current tick doesn't crash on
        // a null, but the next tick re-fetches live.
        if (!string.IsNullOrEmpty(result.Name))
        {
            await WriteCacheAsync(cacheKey, result, settings, ct).ConfigureAwait(false);
        }
        else
        {
            mLog.LogWarning("Lodestone GetFreeCompany parsed empty Name for {Id} — skipping cache write so the refresh queue can retry.", freeCompanyLodestoneId);
        }
        return result;
    }

    public Task<IReadOnlyList<string>?> GetCharacterMountsAsync(ulong lodestoneId, CancellationToken ct = default)
        => GetCharacterCollectableNamesAsync(lodestoneId, "GetCharacterMount", "mounts", ct);

    public Task<IReadOnlyList<string>?> GetCharacterMinionsAsync(ulong lodestoneId, CancellationToken ct = default)
        => GetCharacterCollectableNamesAsync(lodestoneId, "GetCharacterMinion", "minions", ct);

    private async Task<IReadOnlyList<string>?> GetCharacterCollectableNamesAsync(
        ulong lodestoneId, string netStoneMethod, string cacheCategory, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:{cacheCategory}:{lodestoneId}";
        var cached = await TryReadCacheAsync<List<string>>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        if (client is null) return null;

        var method = client.GetType().GetMethod(netStoneMethod, new[] { typeof(string) });
        if (method is null) return null;

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        const int maxHops = 50;

        object? page;
        try
        {
            var task = (Task?)method.Invoke(client, new object[] { lodestoneId.ToString() });
            if (task is null) return results; // reflection drift — let the queue retry & exhaust
            await task.ConfigureAwait(false);
            page = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Lodestone {Method} failed for {Id} page 1", netStoneMethod, lodestoneId);
            return null;
        }

        var hops = 0;
        while (page is not null && hops < maxHops)
        {
            ct.ThrowIfCancellationRequested();
            hops++;
            var added = ExtractCollectableNamesFromPage(page, results, seen);
            if (!added) break;
            try
            {
                page = await InvokeGetNextPageAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mLog.LogWarning(ex, "Lodestone {Method} GetNextPage failed for {Id} after page {N}",
                    netStoneMethod, lodestoneId, hops);
                break;
            }
        }

        await WriteListCacheIfNonEmptyAsync(cacheKey, results, settings, cacheCategory, lodestoneId, ct).ConfigureAwait(false);
        return results;
    }

    private static bool ExtractCollectableNamesFromPage(object page, List<string> sink, HashSet<string> seen)
    {
        var prop = page.GetType().GetProperty("Collectables");
        if (prop?.GetValue(page) is not System.Collections.IEnumerable items) return false;

        var added = false;
        foreach (var item in items)
        {
            if (item is null) continue;
            var name = item.GetType().GetProperty("Name")?.GetValue(item) as string;
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(name)) continue;
            sink.Add(name);
            added = true;
        }
        return added;
    }

    public async Task<IReadOnlyList<LodestoneGearSlot>?> GetCharacterGearAsync(ulong lodestoneId, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.ModuleEnabled) return null;

        var cacheKey = $"lodestone:{LangTag}:gear:{lodestoneId}";
        var cached = await TryReadCacheAsync<List<LodestoneGearSlot>>(cacheKey, settings, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // NetStone 1.4.1's per-slot gear pipeline returns null against current Lodestone
        // HTML, so we scrape the character page + per-slot tooltips directly. Base address
        // follows the active plugin culture so item names come back in the user's language.
        // Reuse NetStone's own HttpClient for the scrape so we share the
        // connection pool / cookies / DNS cache; reflection-pull because
        // NetStone doesn't expose the field publicly. Fallback to a private
        // shared client when the NetStone client isn't ready yet or the
        // field rename breaks our reflection — DirectGearScraper handles
        // the null and uses its own lazy fallback.
        var client = await GetNetClientAsync(ct).ConfigureAwait(false);
        var sharedHttp = client is null ? null : TryGetNetStoneHttpClient(client);
        var scraper = new DirectGearScraper(sharedHttp, mLog);
        var results = await scraper.FetchGearSlotsAsync(lodestoneId, LodestoneBaseAddress, ct).ConfigureAwait(false);

        await WriteListCacheIfNonEmptyAsync(cacheKey, results, settings, "gear", lodestoneId, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Lodestone wraps FC tags in guillemets («ATRS») in the HTML. The tag itself is
    /// a 1–5 char identifier — strip the quote decoration and any padding whitespace
    /// so what we persist matches what the user sees in-game.
    /// </summary>
    private static string? NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().Trim('«', '»').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? TryReadString(object source, string property)
    {
        try
        {
            var prop = source.GetType().GetProperty(property);
            var value = prop?.GetValue(source);
            return value switch
            {
                null => null,
                string s => s,
                _ => value.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NetStone surfaces Gender as the raw Unicode glyph ♀ / ♂,
    /// driven by <c>LodestoneCharacter.FemaleChar</c> and <c>MaleChar</c>
    /// constants. Normalise to the stable strings <c>"Female"</c> /
    /// <c>"Male"</c> so consumers of our public <see cref="CharacterSummary"/>
    /// don't have to compare against unicode codepoints. Reads the constants
    /// via reflection so a NetStone glyph swap still matches; falls back to
    /// the standard Unicode codepoints when reflection can't find the
    /// named members.</summary>
    private static string? NormaliseGender(object netStoneCh)
    {
        var raw = TryReadString(netStoneCh, "Gender");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var type = netStoneCh.GetType();
        var female = TryReadStaticChar(type, "FemaleChar") ?? '♀'; // ♀
        var male = TryReadStaticChar(type, "MaleChar") ?? '♂';     // ♂
        if (raw.IndexOf(female) >= 0) return "Female";
        if (raw.IndexOf(male) >= 0) return "Male";
        return null;
    }

    private static char? TryReadStaticChar(Type type, string memberName)
    {
        try
        {
            var field = type.GetField(memberName,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);
            if (field is null) return null;
            // Works for both const and static readonly char fields.
            var value = field.GetValue(null);
            return value is char c ? c : (char?)null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reflection access to NetStone's internal HttpClient field so
    /// our direct scrapers share the same connection pool / cookies / DNS
    /// cache as NetStone itself. The field is <c>private readonly HttpClient
    /// client</c> on <c>NetStone.LodestoneClient</c> as of v1.4.x; returns
    /// null when reflection fails (e.g. NetStone renames the field across a
    /// major version) so callers can fall back to their own client without
    /// crashing.</summary>
    private static HttpClient? TryGetNetStoneHttpClient(NetStone.LodestoneClient netStone)
    {
        try
        {
            var field = netStone.GetType().GetField("client",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(netStone) as HttpClient;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadInt(object source, string property)
    {
        try
        {
            var prop = source.GetType().GetProperty(property);
            var value = prop?.GetValue(source);
            if (value is null) return null;
            if (value is int i) return i;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryReadDateTime(object source, string property)
    {
        try
        {
            var prop = source.GetType().GetProperty(property);
            return prop?.GetValue(source) as DateTime?;
        }
        catch
        {
            return null;
        }
    }

    // Open vs closed is encoded structurally on Lodestone, not as the inner
    // text: the recruitment <p> carries the extra CSS class
    // `freecompany__recruitment` only when the FC is accepting members
    // (closed FCs drop the class and keep just `freecompany__text`). NetStone's
    // `Recruitment` string XPath selects on that class, so its non-null/
    // non-empty presence is a locale-independent boolean — true for open
    // across every language.
    //
    // (NetStone also exposes a typed `RecruitmentOpen` bool, but its
    // value-matcher hard-codes the English "Open" literal and returns false
    // on every non-English locale even when the FC is actually open. Don't
    // trust it as a primary signal.)
    private static bool? TryReadRecruitmentOpen(object fc)
    {
        var raw = TryReadString(fc, "Recruitment");
        if (!string.IsNullOrWhiteSpace(raw)) return true;

        // Closed FCs miss the class so NetStone returns null. Guard against
        // "page failed to parse" by checking that NetStone produced a Name —
        // if we have it, the page loaded and the missing class means closed.
        var name = TryReadString(fc, "Name");
        if (!string.IsNullOrWhiteSpace(name)) return false;

        return null;
    }

    /// <summary>Classify NetStone's <c>LodestoneFreeCompany.ActiveState</c>
    /// string (DE / EN / FR / JA picklist value) into the typed
    /// <see cref="FreeCompanyActiveState"/>. DE / EN strings are confirmed
    /// against real Lodestone scrapes; FR / JA are best-effort. Unrecognised
    /// values map to <see cref="FreeCompanyActiveState.Unknown"/>.</summary>
    private static FreeCompanyActiveState TryReadActiveState(object fc)
    {
        var raw = TryReadString(fc, "ActiveState");
        if (string.IsNullOrWhiteSpace(raw)) return FreeCompanyActiveState.Unknown;
        var t = raw.Trim();
        if (Eq(t, "Not specified", "Keine Angabe", "Non spécifié", "指定なし"))
            return FreeCompanyActiveState.NotSpecified;
        if (Eq(t, "Always", "Jeden Tag", "Tous les jours", "毎日"))
            return FreeCompanyActiveState.Always;
        if (Eq(t, "Weekdays", "Wochentags", "En semaine", "平日"))
            return FreeCompanyActiveState.Weekdays;
        if (Eq(t, "Weekends", "Wochenende", "Le week-end", "週末"))
            return FreeCompanyActiveState.Weekends;
        return FreeCompanyActiveState.Unknown;

        static bool Eq(string t, params string[] options)
        {
            foreach (var o in options)
                if (string.Equals(t, o, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    private static LodestoneFreeCompanyEstate? TryReadEstate(object fc)
    {
        try
        {
            var estateProp = fc.GetType().GetProperty("Estate");
            var estate = estateProp?.GetValue(fc);
            if (estate is null) return null;

            var name = TryReadString(estate, "Name");
            var rawPlot = TryReadString(estate, "Plot");
            var greeting = TryReadString(estate, "Greeting");

            // Bail early if every estate field is absent — Lodestone's housing
            // block is missing entirely for FCs that don't own property.
            if (name is null && rawPlot is null && greeting is null) return null;

            var parsed = ParsePlotString(rawPlot);
            return new LodestoneFreeCompanyEstate
            {
                Name = name,
                Greeting = greeting,
                DistrictName = parsed.DistrictName,
                Ward = parsed.Ward,
                PlotNumber = parsed.PlotNumber,
                HouseSize = parsed.HouseSize,
                IsSubdivision = parsed.IsSubdivision,
            };
        }
        catch
        {
            return null;
        }
    }

    // Parses a Lodestone-scraped housing "Plot" string into structured fields.
    // The string format is locale-dependent — Lodestone publishes whatever the
    // user's region renders. Examples we handle:
    //   EN: "Plot 14, 6 Ward, Lavender Beds (Faerie)"
    //   EN: "Plot 14, 6 Ward, Lavender Beds Subdivision (Faerie)"
    //   DE: "Dorf des Nebels 17. Bezirk, 15 [Groß]"
    // FR/JP fall through to the empty result — callers leave the structured
    // columns null and DbInspect can backfill once a format sample is added.
    private static readonly Regex EnPlotRegex = new(
        @"^Plot\s+(?<plot>\d+),\s*(?<ward>\d+)\s+Ward,\s*(?<district>.+?)(?<sub>\s+Subdivision)?(?:\s*\([^)]+\))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DePlotRegex = new(
        @"^(?<district>.+?)(?<sub>\s+Erweiterung)?\s+(?<ward>\d+)\.\s*Bezirk,\s*(?<plot>\d+)(?:\s*\[(?<size>[^\]]+)\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly record struct PlotParts(
        string? DistrictName,
        int? Ward,
        int? PlotNumber,
        HouseSize? HouseSize,
        bool IsSubdivision);

    private static PlotParts ParsePlotString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return default;
        var s = raw.Trim();

        var m = EnPlotRegex.Match(s);
        if (!m.Success) m = DePlotRegex.Match(s);
        if (!m.Success) return default;

        int? ward = int.TryParse(m.Groups["ward"].Value, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out var w) ? w : null;
        int? plot = int.TryParse(m.Groups["plot"].Value, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out var p) ? p : null;
        var district = m.Groups["district"].Value.Trim();
        if (district.Length == 0) district = null!;
        var size = MapHouseSize(m.Groups["size"].Success ? m.Groups["size"].Value : null);
        var isSub = m.Groups["sub"].Success;

        return new PlotParts(district, ward, plot, size, isSub);
    }

    private static HouseSize? MapHouseSize(string? bracketed)
    {
        if (string.IsNullOrWhiteSpace(bracketed)) return null;
        var t = bracketed.Trim().ToLowerInvariant();
        if (t.Contains("klein") || t.Contains("small") || t.Contains("cottage") || t.Contains("petit"))
            return HouseSize.Small;
        if (t.Contains("mittel") || t.Contains("medium") || t.Contains("mid") || t.Contains("maison"))
            return HouseSize.Medium;
        if (t.Contains("groß") || t.Contains("gross") || t.Contains("large") || t.Contains("mansion") || t.Contains("manoir"))
            return HouseSize.Large;
        return null;
    }

    private static LodestoneFreeCompanyFocus? TryReadFocus(object fc)
    {
        try
        {
            var focusProp = fc.GetType().GetProperty("Focus");
            var focus = focusProp?.GetValue(fc);
            if (focus is null) return null;
            return new LodestoneFreeCompanyFocus
            {
                RolePlay = ReadFocusFlag(focus, "RolePlay"),
                Leveling = ReadFocusFlag(focus, "Leveling"),
                Casual = ReadFocusFlag(focus, "Casual"),
                Hardcore = ReadFocusFlag(focus, "Hardcore"),
                Dungeons = ReadFocusFlag(focus, "Dungeons"),
                Guildhests = ReadFocusFlag(focus, "Guildhests"),
                Trials = ReadFocusFlag(focus, "Trials"),
                Raids = ReadFocusFlag(focus, "Raids"),
                PvP = ReadFocusFlag(focus, "PvP"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadFocusFlag(object focus, string name)
    {
        try
        {
            var prop = focus.GetType().GetProperty(name);
            var value = prop?.GetValue(focus);
            if (value is null) return false;
            var enabledProp = value.GetType().GetProperty("IsEnabled");
            return enabledProp?.GetValue(value) is true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadSocialId(object character, string propertyName)
        => TryReadSocialMember(character, propertyName, "Id");

    private static string? TryReadSocialName(object character, string propertyName)
        => TryReadSocialMember(character, propertyName, "Name");

    private static string? TryReadSocialMember(object character, string propertyName, string memberName)
    {
        try
        {
            var prop = character.GetType().GetProperty(propertyName);
            var social = prop?.GetValue(character);
            if (social is null) return null;
            var member = social.GetType().GetProperty(memberName);
            return member?.GetValue(social) as string;
        }
        catch
        {
            return null;
        }
    }

    private async Task<NetStone.LodestoneClient?> GetNetClientAsync(CancellationToken ct)
    {
        var wantedBase = LodestoneBaseAddress;
        if (mNetClient is not null && mNetClientBaseAddress == wantedBase) return mNetClient;

        await mNetClientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (mNetClient is not null && mNetClientBaseAddress == wantedBase) return mNetClient;
            mNetClient = await NetStone.LodestoneClient.GetClientAsync(lodestoneBaseAddress: wantedBase).ConfigureAwait(false);
            mNetClientBaseAddress = wantedBase;
            return mNetClient;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "Failed to initialize NetStone LodestoneClient (base={Base}).", wantedBase);
            return null;
        }
        finally
        {
            mNetClientLock.Release();
        }
    }

    private async Task<LodestoneSettings> GetSettingsAsync(CancellationToken ct)
    {
        return await mSettingsStore.GetAsync<LodestoneSettings>(LodestoneSettings.StoreKey, ct).ConfigureAwait(false)
            ?? new LodestoneSettings();
    }

    private async Task<T?> TryReadCacheAsync<T>(string key, LodestoneSettings settings, CancellationToken ct)
        where T : class
    {
        if (!settings.CacheEnabled) return null;

        await using var ctx = await mDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Set<LodestoneCacheEntity>().FindAsync([key], cancellationToken: ct).ConfigureAwait(false);
        if (row is null) return null;
        if (row.ExpiresAt <= DateTime.UtcNow) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(row.Response);
        }
        catch (JsonException ex)
        {
            mLog.LogDebug(ex, "Lodestone cache row for {Key} was unreadable; will refetch.", key);
            return null;
        }
    }

    /// <summary>Writes a list-shaped result to the cache only when it has at
    /// least one element. Empty lists are ambiguous between "scrape failed
    /// silently" and "legit zero (new character, private profile, …)" — caching
    /// either kind would block re-fetches for the full TTL. Skipping leaves
    /// the row absent so the refresh queue's per-category retry can attempt up
    /// to <c>MaxAttempts</c> times before giving up via <c>ExhaustedAttempts</c>.</summary>
    private async Task WriteListCacheIfNonEmptyAsync<T>(
        string key, IReadOnlyCollection<T> value, LodestoneSettings settings,
        string category, ulong lodestoneId, CancellationToken ct)
    {
        if (value.Count > 0)
        {
            await WriteCacheAsync(key, value, settings, ct).ConfigureAwait(false);
            return;
        }
        mLog.LogInformation(
            "Lodestone {Category} returned empty for {Id} — skipping cache write so the refresh queue can retry (up to MaxAttempts).",
            category, lodestoneId);
    }

    private async Task WriteCacheAsync<T>(string key, T value, LodestoneSettings settings, CancellationToken ct)
    {
        if (!settings.CacheEnabled) return;

        var json = JsonSerializer.Serialize(value);
        var now = DateTime.UtcNow;
        var expires = now.AddHours(Math.Max(1, settings.CacheTtlHours));

        await using var ctx = await mDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.Set<LodestoneCacheEntity>().FindAsync([key], cancellationToken: ct).ConfigureAwait(false);
        if (existing is null)
        {
            ctx.Set<LodestoneCacheEntity>().Add(new LodestoneCacheEntity
            {
                Key = key,
                Response = json,
                FetchedAt = now,
                ExpiresAt = expires,
            });
        }
        else
        {
            existing.Response = json;
            existing.FetchedAt = now;
            existing.ExpiresAt = expires;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}