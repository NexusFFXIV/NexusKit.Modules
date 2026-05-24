using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NexusKit.Core.Cache;
using NexusKit.Modules.FfxivCollect.Persistence;
using NexusKit.Persistence;

namespace NexusKit.Modules.FfxivCollect.Cache;

/// <summary>
/// <see cref="IExternalDataCacheResetter"/> implementation for the
/// FFXIVCollect module. Cache keys live as
/// <c>ffxivcollect:{lang}:characters/{lid}</c> (bare identity) or
/// <c>ffxivcollect:{lang}:characters/{lid}/&lt;resource&gt;</c> (sub-resource).
/// Catalog keys (<c>ffxivcollect:{lang}:mounts</c>, etc.) aren't keyed on a
/// player and intentionally stay untouched.
/// </summary>
internal sealed class FfxivCollectCacheResetter : IExternalDataCacheResetter
{
    private readonly INexusDbContextFactory mDb;
    private readonly ILogger<FfxivCollectCacheResetter> mLog;

    public FfxivCollectCacheResetter(INexusDbContextFactory db, ILogger<FfxivCollectCacheResetter> log)
    {
        mDb = db;
        mLog = log;
    }

    public async Task<int> ResetAsync(ResetContext ctx, CancellationToken ct = default)
    {
        try
        {
            await using var db = await mDb.CreateDbContextAsync(ct).ConfigureAwait(false);
            // Two patterns: the bare characters/{lid} row plus every
            // characters/{lid}/<sub> row. Splitting them keeps each LIKE
            // tight (no leading "%" wildcard that would force a full scan).
            var bare = $"ffxivcollect:%:characters/{ctx.LodestoneId}";
            var sub = $"ffxivcollect:%:characters/{ctx.LodestoneId}/%";
            var deleted = await db.Set<FfxivCollectCacheEntity>()
                .Where(e => EF.Functions.Like(e.Key, bare)
                         || EF.Functions.Like(e.Key, sub))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return deleted;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "FFXIVCollect cache reset failed for lid {Lid}", ctx.LodestoneId);
            return 0;
        }
    }
}
