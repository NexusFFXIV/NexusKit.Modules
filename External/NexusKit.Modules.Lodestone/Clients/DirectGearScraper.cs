using System.Net.Http;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NexusKit.Modules.Lodestone.Models;

namespace NexusKit.Modules.Lodestone.Clients;

/// <summary>
/// HTML scraper for per-slot Lodestone equipment tooltips. NetStone 1.4.1's gear
/// parsing returns null against current Lodestone HTML, so we fetch the character page
/// plus each <c>/lodestone/character/{id}/item_detail/{slot}</c> tooltip directly and
/// parse with HtmlAgilityPack via XPath (no CSS-selector add-on needed).
/// <para>Slot layout: Lodestone indexes 0..13 with slot 5 (waist) deprecated. We map
/// onto our 0..12 contiguous indexing (slot 6..13 shift down by one).</para>
/// </summary>
internal sealed class DirectGearScraper
{
    // U+E03C is the FFXIV private-use HQ icon Lodestone appends to HQ item names.
    private const char HqMarker = '';

    // Lazy fallback — only constructed if the caller didn't supply a client
    // (i.e. NetStone reflection-access failed). Static so the fallback
    // socket pool gets re-used across scraper instances. User-Agent shaped
    // like a normal desktop browser so Lodestone serves the standard HTML
    // variant rather than a mobile/throttled one.
    private static readonly Lazy<HttpClient> FallbackHttp = new(CreateFallbackHttpClient);

    private readonly HttpClient mHttp;
    private readonly ILogger mLog;

    /// <summary>
    /// Construct with an explicit <see cref="HttpClient"/> — caller is expected
    /// to pass NetStone's internal client (via reflection in
    /// <c>LodestoneClient</c>) so both NetStone and this scraper share the
    /// same connection pool, cookies and DNS cache. Passing null falls back to
    /// a shared private client.
    /// </summary>
    public DirectGearScraper(HttpClient? http, ILogger log)
    {
        mHttp = http ?? FallbackHttp.Value;
        mLog = log;
    }

    private static HttpClient CreateFallbackHttpClient()
    {
        // BaseAddress isn't set — the regional sub-domain is picked per call (German plugin
        // → de., French → fr., etc.) so item names match the user's language.
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return c;
    }

    public async Task<List<LodestoneGearSlot>> FetchGearSlotsAsync(ulong lodestoneId, string baseAddress, CancellationToken ct)
    {
        var results = new List<LodestoneGearSlot>();
        try
        {
            var charUrl = $"{baseAddress.TrimEnd('/')}/lodestone/character/{lodestoneId}/";
            string charHtml;
            using (var resp = await mHttp.GetAsync(charUrl, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    mLog.LogWarning("DirectGear: character page returned {Status} for {Id}", (int)resp.StatusCode, lodestoneId);
                    return results;
                }
                charHtml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            var charDoc = new HtmlDocument();
            charDoc.LoadHtml(charHtml);

            for (var ldsSlot = 0; ldsSlot <= 13; ldsSlot++)
            {
                ct.ThrowIfCancellationRequested();
                if (ldsSlot == 5) continue; // waist deprecated
                var ourSlot = ldsSlot < 5 ? ldsSlot : ldsSlot - 1;

                var iconNode = charDoc.DocumentNode.SelectSingleNode(
                    $"//*[contains(concat(' ', normalize-space(@class), ' '), ' icon-c--{ldsSlot} ')]");
                var lazyUrl = iconNode?.GetAttributeValue("data-lazy_load_url", string.Empty);
                if (string.IsNullOrEmpty(lazyUrl)) continue;

                // Tooltip URLs Lodestone hands back are relative — bind them to the same
                // regional base address so the tooltip body stays in the user's language.
                var tooltipUrl = lazyUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? lazyUrl
                    : $"{baseAddress.TrimEnd('/')}{lazyUrl}";

                string tooltipHtml;
                try
                {
                    using var resp = await mHttp.GetAsync(tooltipUrl, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;
                    tooltipHtml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(tooltipHtml)) continue;

                var tipDoc = new HtmlDocument();
                tipDoc.LoadHtml(tooltipHtml);

                var nameNode = FindByClass(tipDoc.DocumentNode, "db-tooltip__item__name");
                var itemName = HtmlEntity.DeEntitize(nameNode?.InnerText?.Trim() ?? string.Empty);
                if (string.IsNullOrEmpty(itemName)) continue;

                var isHq = itemName.IndexOf(HqMarker) >= 0;
                if (isHq) itemName = itemName.Replace(HqMarker.ToString(), string.Empty).Trim();

                string? glamourName = null;
                var mirageRoot = FindByClass(tipDoc.DocumentNode, "db-tooltip__item__mirage");
                var mirageP = mirageRoot?.SelectSingleNode(".//p");
                if (mirageP != null)
                {
                    var raw = HtmlEntity.DeEntitize(mirageP.InnerText?.Trim() ?? string.Empty);
                    if (!string.IsNullOrEmpty(raw)) glamourName = raw;
                }

                // Dye colors live in <li class="stain"> rows under .list_1col. FFXIV supports
                // up to two dyes per item, rendered as "Farbe 1" / "Farbe 2". Collect every
                // <a> in those rows; empty list when nothing is dyed.
                var colors = new List<string>();
                var stainList = FindByClass(tipDoc.DocumentNode, "list_1col");
                if (stainList != null)
                {
                    var stainRows = stainList.SelectNodes(
                        ".//*[contains(concat(' ', normalize-space(@class), ' '), ' stain ')]");
                    if (stainRows != null)
                    {
                        foreach (var row in stainRows)
                        {
                            var anchor = row.SelectSingleNode(".//a");
                            if (anchor is null) continue;
                            var t = HtmlEntity.DeEntitize(anchor.InnerText?.Trim() ?? string.Empty);
                            if (!string.IsNullOrEmpty(t)) colors.Add(t);
                        }
                    }
                }

                var materiaNodes = tipDoc.DocumentNode.SelectNodes(
                    "//ul[contains(concat(' ', normalize-space(@class), ' '), ' db-tooltip__materia ')]/li" +
                    "//*[contains(concat(' ', normalize-space(@class), ' '), ' db-tooltip__materia__txt ')]");
                var materia = (materiaNodes ?? Enumerable.Empty<HtmlNode>())
                    .Select(n => HtmlEntity.DeEntitize(n.FirstChild?.InnerText?.Trim() ?? string.Empty))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                string? creatorName = null;
                var sigNode = FindByClass(tipDoc.DocumentNode, "db-tooltip__signature")
                              ?? FindByClass(tipDoc.DocumentNode, "db-tooltip__item_signature")
                              ?? FindByClass(tipDoc.DocumentNode, "db-tooltip__item__signature");
                if (sigNode != null)
                {
                    var t = HtmlEntity.DeEntitize(sigNode.InnerText?.Trim() ?? string.Empty);
                    if (!string.IsNullOrEmpty(t)) creatorName = t;
                }

                int? itemLevel = null;
                var lvlNode = FindByClass(tipDoc.DocumentNode, "db-tooltip__item__level");
                if (lvlNode != null)
                {
                    var lvlMatch = Regex.Match(lvlNode.InnerText ?? string.Empty, "\\d+");
                    if (lvlMatch.Success && int.TryParse(lvlMatch.Value, out var lvl))
                        itemLevel = lvl;
                }

                results.Add(new LodestoneGearSlot
                {
                    SlotIndex = ourSlot,
                    SlotName = SlotName(ourSlot),
                    ItemName = itemName,
                    IsHq = isHq,
                    GlamourName = glamourName,
                    Colors = colors,
                    Materia = materia,
                    CreatorName = creatorName,
                    ItemLevel = itemLevel,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "DirectGear fetch failed for {Id}", lodestoneId);
        }
        return results;
    }

    /// <summary>Selects the first descendant whose <c>class</c> attribute contains the
    /// given whole-word class name. Avoids false positives that <c>contains(@class,…)</c>
    /// alone would produce on partial matches.</summary>
    private static HtmlNode? FindByClass(HtmlNode root, string className)
        => root.SelectSingleNode(
            $".//*[contains(concat(' ', normalize-space(@class), ' '), ' {className} ')]");

    private static string SlotName(int index) => index switch
    {
        0  => "Mainhand",
        1  => "Offhand",
        2  => "Head",
        3  => "Body",
        4  => "Hands",
        5  => "Legs",
        6  => "Feet",
        7  => "Earrings",
        8  => "Necklace",
        9  => "Bracelets",
        10 => "Ring1",
        11 => "Ring2",
        12 => "SoulCrystal",
        _  => $"Slot{index}",
    };
}
