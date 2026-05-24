using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core.Localization;
using NexusKit.Modules.FfxivCollect.Models;
using NexusKit.Modules.FfxivCollect.Persistence;
using NexusKit.Persistence;
using NexusKit.Persistence.Settings;

namespace NexusKit.Modules.FfxivCollect.Clients;

internal sealed class FfxivCollectClient : IFfxivCollectClient
{
    public const string HttpClientName = "nexuskit-ffxivcollect";

    private readonly IHttpClientFactory mHttpFactory;
    private readonly ISettingsStore mSettingsStore;
    private readonly INexusDbContextFactory mDbFactory;
    private readonly LocalizationManager? mLocalization;
    private readonly ILogger<FfxivCollectClient> mLog;

    public FfxivCollectClient(
        IHttpClientFactory httpFactory,
        ISettingsStore settingsStore,
        INexusDbContextFactory dbFactory,
        ILogger<FfxivCollectClient> log,
        LocalizationManager? localization = null)
    {
        mHttpFactory = httpFactory;
        mSettingsStore = settingsStore;
        mDbFactory = dbFactory;
        mLog = log;
        mLocalization = localization;
        // Session-startup cleanup of expired cache rows — same pattern as
        // LodestoneClient. Read-side already skips expired entries but never
        // deletes them, so without this the table grew with every refreshed
        // ownership lookup.
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
            var deleted = await ctx.Set<FfxivCollectCacheEntity>()
                .Where(e => e.ExpiresAt < now)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                mLog.LogInformation("FFXIVCollect cache: pruned {Count} expired row(s) at session start.", deleted);
        }
        catch (OperationCanceledException) { /* plugin shutdown */ }
        catch (Exception ex)
        {
            mLog.LogDebug(ex, "FFXIVCollect cache: expired-row prune failed at session start.");
        }
    }

    /// <summary>
    /// FFXIVCollect's <c>?lang=</c> query parameter localizes mount / minion / achievement
    /// names. Returns the short tag (<c>de</c>, <c>fr</c>, <c>ja</c>) or null for English
    /// (the API default — no query needed).
    /// </summary>
    private string? LangParam
    {
        get
        {
            var two = (mLocalization?.CurrentCulture ?? CultureInfo.CurrentUICulture)
                .TwoLetterISOLanguageName?.ToLowerInvariant();
            return two switch
            {
                "de" => "de",
                "fr" => "fr",
                "ja" => "ja",
                _    => null,
            };
        }
    }

    private string LangTag => LangParam ?? "en";

    public Task<Character?> GetCharacterAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default)
        => FetchAsync<Character>($"characters/{lodestoneId}", forceLatest, ct);

    public Task<ListResponse<Mount>?> GetMountsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default)
        => FetchAsync<ListResponse<Mount>>($"characters/{lodestoneId}/mounts", forceLatest, ct);

    public Task<ListResponse<Minion>?> GetMinionsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default)
        => FetchAsync<ListResponse<Minion>>($"characters/{lodestoneId}/minions", forceLatest, ct);

    public Task<ListResponse<Achievement>?> GetAchievementsAsync(ulong lodestoneId, bool forceLatest = false, CancellationToken ct = default)
        => FetchAsync<ListResponse<Achievement>>($"characters/{lodestoneId}/achievements", forceLatest, ct);

    public Task<ListResponse<Mount>?> GetMountCatalogAsync(CancellationToken ct = default)
        => FetchAsync<ListResponse<Mount>>("mounts", forceLatest: false, ct);

    public Task<ListResponse<Minion>?> GetMinionCatalogAsync(CancellationToken ct = default)
        => FetchAsync<ListResponse<Minion>>("minions", forceLatest: false, ct);

    public Task<ListResponse<Achievement>?> GetAchievementCatalogAsync(CancellationToken ct = default)
        => FetchAsync<ListResponse<Achievement>>("achievements", forceLatest: false, ct);

    public Task<Mount?> GetMountAsync(int id, CancellationToken ct = default)
        => FetchAsync<Mount>($"mounts/{id}", forceLatest: false, ct);

    public Task<Minion?> GetMinionAsync(int id, CancellationToken ct = default)
        => FetchAsync<Minion>($"minions/{id}", forceLatest: false, ct);

    public Task<Achievement?> GetAchievementAsync(int id, CancellationToken ct = default)
        => FetchAsync<Achievement>($"achievements/{id}", forceLatest: false, ct);

    private async Task<T?> FetchAsync<T>(string endpoint, bool forceLatest, CancellationToken ct) where T : class
    {
        var settings = await mSettingsStore.GetAsync<FfxivCollectSettings>(FfxivCollectSettings.StoreKey, ct).ConfigureAwait(false)
            ?? new FfxivCollectSettings();

        if (!settings.ModuleEnabled)
            return null;

        var lang = LangParam;
        // forceLatest adds FFXIVCollect's ?latest=true query so the upstream
        // server re-syncs from Lodestone before answering. Combine with the
        // language param when present; both are simple query keys.
        var qs = new List<string>(2);
        if (lang is not null) qs.Add($"language={lang}");
        if (forceLatest) qs.Add("latest=true");
        var endpointWithQuery = qs.Count == 0 ? endpoint : $"{endpoint}?{string.Join('&', qs)}";

        // Cache key stays language-aware but is independent of forceLatest —
        // the only difference is that forceLatest skips the cache *read*
        // (forces network) while still *writing* the fresh body back into the
        // same row, so subsequent non-latest reads see the new data instead
        // of the stale pre-refresh body.
        var cacheKey = $"ffxivcollect:{LangTag}:{endpoint}";

        if (settings.CacheEnabled && !forceLatest)
        {
            var cached = await ReadCacheAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(cached);
                }
                catch (JsonException ex)
                {
                    mLog.LogWarning(ex, "Cached FFXIVCollect response for {Endpoint} was unreadable; refetching.", endpoint);
                }
            }
        }

        var url = $"{settings.BaseUrl.TrimEnd('/')}/{endpointWithQuery}";
        string body;
        try
        {
            var http = mHttpFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                mLog.LogDebug("FFXIVCollect {Endpoint} returned {Status}", endpoint, (int)response.StatusCode);
                return null;
            }
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mLog.LogWarning(ex, "FFXIVCollect fetch failed for {Endpoint}", endpoint);
            return null;
        }

        T? deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException ex)
        {
            mLog.LogWarning(ex, "FFXIVCollect response for {Endpoint} could not be deserialized.", endpoint);
            return null;
        }

        // Cache-poison guard mirroring the Lodestone client: a 200 OK can
        // still carry a degenerate Character body with no name, and writing
        // that to cache wedges the identity waterfall in
        // ExternalDataPlayerService for the full TTL. List endpoints are
        // exempt — an empty results array is a legitimate response shape
        // (private profile, brand-new character with no mounts yet) and
        // rejecting it would spam the queue with retries that never
        // succeed.
        if (settings.CacheEnabled && IsCacheable(deserialized))
        {
            await WriteCacheAsync(cacheKey, body, settings.CacheTtlHours, ct).ConfigureAwait(false);
        }
        else if (deserialized is Character degenerate
                 && string.IsNullOrEmpty(degenerate.Name))
        {
            mLog.LogWarning("FFXIVCollect {Endpoint} returned empty Name — skipping cache write so the refresh queue can retry.", endpoint);
        }

        return deserialized;
    }

    /// <summary>Block the cache write when the deserialised payload is
    /// semantically empty in a way that would poison downstream identity
    /// resolution. Only meaningful for the bare <c>characters/{id}</c>
    /// endpoint today — every other shape's "empty" response is a legitimate
    /// state and gets cached normally.</summary>
    private static bool IsCacheable<T>(T? value) where T : class
    {
        if (value is null) return false;
        if (value is Character ch) return !string.IsNullOrEmpty(ch.Name);
        return true;
    }

    private async Task<string?> ReadCacheAsync(string key, CancellationToken ct)
    {
        await using var ctx = await mDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await ctx.Set<FfxivCollectCacheEntity>().FindAsync([key], cancellationToken: ct).ConfigureAwait(false);
        if (row is null) return null;
        if (row.ExpiresAt <= DateTime.UtcNow) return null;
        return row.Response;
    }

    private async Task WriteCacheAsync(string key, string body, int ttlHours, CancellationToken ct)
    {
        await using var ctx = await mDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await ctx.Set<FfxivCollectCacheEntity>().FindAsync([key], cancellationToken: ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var expires = now.AddHours(Math.Max(1, ttlHours));

        if (existing is null)
        {
            ctx.Set<FfxivCollectCacheEntity>().Add(new FfxivCollectCacheEntity
            {
                Key = key,
                Response = body,
                FetchedAt = now,
                ExpiresAt = expires,
            });
        }
        else
        {
            existing.Response = body;
            existing.FetchedAt = now;
            existing.ExpiresAt = expires;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}