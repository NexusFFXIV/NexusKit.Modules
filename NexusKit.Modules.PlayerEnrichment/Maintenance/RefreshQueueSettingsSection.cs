using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using NexusKit.Core.Localization;
using NexusKit.Modules.InternalData.Refresh;
using NexusKit.Persistence.Maintenance;
using NexusKit.Persistence.Settings;
using NexusKit.Ui.AutoSettings;
using NexusKit.Ui.Utilities;
using NexusKit.Ui.Widgets;

namespace NexusKit.Modules.PlayerEnrichment.Maintenance;

/// <summary>
/// Module-provided IAutoSettingsSection for refresh-queue diagnostics:
/// total + active/exhausted split, distribution by priority and category,
/// estimated drain time at the worker's current spacing, and a top-N list
/// of the content_ids with the deepest pending queues.
///
/// <para>Lives in <c>NexusKit.Modules.PlayerEnrichment</c> so any plugin
/// that wires <c>AddNexusKitPlayerEnrichment()</c> + <c>AddNexusKitUi()</c>
/// gets the section automatically — no plugin-side glue required.</para>
///
/// <para>Subscribes to <see cref="IDbStatsService.StatsRefreshed"/> so a
/// "Refresh stats" / "Run maintenance now" click on the shared
/// DB-maintenance section invalidates this section's snapshot too — the
/// user sees fresh queue numbers on the next render without having to
/// re-trigger this section's own Refresh button.</para>
/// </summary>
public sealed class RefreshQueueSettingsSection : IAutoSettingsSection, IDisposable
{
    public int Order { get; }
    public string NavTitleKey => "nexuskit.modules.playerenrichment.queuestats.section.nav";

    private readonly IRefreshQueueStatsService mStats;
    private readonly IDbStatsService mDbStats;
    private readonly RefreshCategoryPolicy mPolicy;
    private readonly ILocalizer mLoc;

    // Auto-refresh cadence while the section is being rendered. The worker
    // ticks every WorkerGap=2s and the DB query is sub-10ms, so polling at
    // 3s is cheap and keeps counts / countdown live without any event
    // plumbing from the queue service. Targets the case where the user
    // opens the section and watches as exhausted rows climb, cooldown
    // clears, etc. — none of which the per-frame countdown alone reveals.
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

    private RefreshQueueStatsSnapshot? mSnapshot;
    private Task? mLoadTask;
    private DateTime mLastLoadAt = DateTime.MinValue;
    private bool mDisposed;

    public RefreshQueueSettingsSection(
        IRefreshQueueStatsService stats,
        IDbStatsService dbStats,
        RefreshCategoryPolicy policy,
        ILocalizer localizer,
        int order = 210)
    {
        mStats = stats;
        mDbStats = dbStats;
        mPolicy = policy;
        mLoc = localizer;
        Order = order;

        mDbStats.StatsRefreshed += OnDbStatsRefreshed;
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;
        mDbStats.StatsRefreshed -= OnDbStatsRefreshed;
    }

    /// <summary>Invalidate the cached snapshot when the DB-maintenance
    /// section's "Refresh stats" / "Run maintenance now" runs. The next
    /// Render call sees the null snapshot and kicks our own StartLoad.
    /// Runs on the DbStats gather task — no UI work allowed here.</summary>
    private void OnDbStatsRefreshed() => mSnapshot = null;

    public void Render(ISettingsStore store)
    {
        // First-time load OR snapshot older than AutoRefreshInterval. The
        // generic time-based check handles every state transition: counts
        // climbing as the worker fails rows, exhausted-cap crossings,
        // cooldown clearing, queue draining. A trigger keyed to
        // EarliestRetryAt alone misses everything that happens between
        // cooldown windows.
        if (mLoadTask is null
            && (mSnapshot is null
                || DateTime.UtcNow - mLastLoadAt > AutoRefreshInterval))
        {
            StartLoad();
        }

        ImGui.TextWrapped(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.section.description"));
        ImGui.Spacing();

        ImGui.BeginDisabled(mLoadTask is not null);
        if (ImGui.Button(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.button.refresh")))
            StartLoad();
        ImGui.EndDisabled();
        if (mLoadTask is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.loading"));
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCategoryToggles();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (mSnapshot is not { } snap)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.loading"));
            return;
        }

        DrawTotals(snap);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDistribution(snap);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawTopContents(snap);
    }

    private void DrawCategoryToggles()
    {
        ImGui.TextDisabled(mLoc.Get("nexuskit.modules.playerenrichment.refresh.toggles.heading"));
        ImGui.TextWrapped(mLoc.Get("nexuskit.modules.playerenrichment.refresh.toggles.description"));
        ImGui.Spacing();

        // Mandatory categories (LodestoneId, FreeCompany) aren't rendered —
        // they can't be turned off so a toggle would be confusing.
        // Three-column grid via NexusGroupBox.DrawGrid: stretches across the
        // section's width so a wide settings window doesn't waste space on
        // six vertically-stacked checkboxes. `s` and `changed` are captured
        // by the lambdas; the per-cell `v` local exists so we can `ref` it
        // into ImGui.Checkbox (auto-properties can't take ref directly).
        var s = mPolicy.Settings;
        var changed = false;

        NexusGroupBox.DrawGrid("##nx_refresh_cat_grid", perRow: 3,
            () => { var v = s.Profile;      if (DrawToggle("profile", ref v))      { s.Profile = v;      changed = true; } },
            () => { var v = s.ClassJobs;    if (DrawToggle("classjobs", ref v))    { s.ClassJobs = v;    changed = true; } },
            () => { var v = s.Gear;         if (DrawToggle("gear", ref v))         { s.Gear = v;         changed = true; } },
            () => { var v = s.Mounts;       if (DrawToggle("mounts", ref v))       { s.Mounts = v;       changed = true; } },
            () => { var v = s.Minions;      if (DrawToggle("minions", ref v))      { s.Minions = v;      changed = true; } },
            () => { var v = s.Achievements; if (DrawToggle("achievements", ref v)) { s.Achievements = v; changed = true; } });

        if (changed) _ = mPolicy.PersistAsync();
    }

    private bool DrawToggle(string suffix, ref bool value)
    {
        var label = mLoc.Get("nexuskit.modules.playerenrichment.refresh.category." + suffix);
        return ImGui.Checkbox($"{label}##nx_refresh_cat_{suffix}", ref value);
    }

    private void DrawTotals(RefreshQueueStatsSnapshot snap)
    {
        DrawStatusLine(snap);
        ImGui.Spacing();

        // Two-column grid keeps the six counter cells visually grouped so the
        // section reads as one block of numbers rather than six stacked lines.
        // The "Next retry" cell is conditional: when no row is in cooldown,
        // EarliestRetryAt is null and the cell is dropped so the grid doesn't
        // show a stale countdown.
        NexusGroupBox.DrawGrid("##nx_queuestats_totals", perRow: 2,
            () => DrawCountLine("queuestats.total", snap.TotalRows),
            () => DrawCountLine("queuestats.exhausted", snap.ExhaustedRows,
                snap.ExhaustedRows > 0 ? ImGuiColors.DalamudYellow : null),
            () => DrawCountLine("queuestats.active", snap.EligibleNowRows),
            () => DrawCountLine("queuestats.waiting", snap.BackoffWaitingRows,
                snap.BackoffWaitingRows > 0 ? ImGuiColors.DalamudGrey : null),
            () => DrawEtaLine(snap),
            snap.EarliestRetryAt is { } retryAt ? () => DrawNextRetryLine(retryAt) : null);
    }

    /// <summary>Single-line worker-state summary, color-coded so the user can
    /// tell "actively grinding" from "idle, waiting on retries" from
    /// "permanently stuck" without parsing the counter grid below.</summary>
    private void DrawStatusLine(RefreshQueueStatsSnapshot snap)
    {
        if (snap.TotalRows == 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.status.empty"));
            return;
        }
        if (snap.EligibleNowRows > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, string.Format(CultureInfo.CurrentCulture,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.status.processing"),
                DurationFormat.TwoUnit(snap.EstimatedDrain)));
            return;
        }
        if (snap.BackoffWaitingRows > 0 && snap.EarliestRetryAt is { } retryAt)
        {
            // Recomputed every frame so the countdown ticks down between
            // Refresh clicks. DurationFormat.TwoUnit clamps to "0s" once
            // the cooldown has lapsed but the snapshot hasn't refreshed.
            var remaining = retryAt - DateTime.UtcNow;
            ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(CultureInfo.CurrentCulture,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.status.idle.cooldown"),
                DurationFormat.TwoUnit(remaining)));
            return;
        }
        if (snap.ExhaustedRows > 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, string.Format(CultureInfo.CurrentCulture,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.status.idle.exhausted"),
                snap.ExhaustedRows));
        }
    }

    private void DrawCountLine(string keySuffix, int count, System.Numerics.Vector4? color = null)
    {
        var text = string.Format(CultureInfo.CurrentCulture,
            mLoc.Get("nexuskit.modules.playerenrichment." + keySuffix), count);
        if (color is { } c) ImGui.TextColored(c, text);
        else ImGui.TextUnformatted(text);
    }

    private void DrawEtaLine(RefreshQueueStatsSnapshot snap)
    {
        var value = snap.EligibleNowRows > 0
            ? DurationFormat.TwoUnit(snap.EstimatedDrain)
            : mLoc.Get("nexuskit.modules.playerenrichment.queuestats.eta.idle");
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture,
            mLoc.Get("nexuskit.modules.playerenrichment.queuestats.eta"), value));
    }

    private void DrawNextRetryLine(DateTime earliestRetryAtUtc)
    {
        var remaining = earliestRetryAtUtc - DateTime.UtcNow;
        var localTime = earliestRetryAtUtc.ToLocalTime();
        ImGui.TextUnformatted(string.Format(CultureInfo.CurrentCulture,
            mLoc.Get("nexuskit.modules.playerenrichment.queuestats.next_retry"),
            DurationFormat.TwoUnit(remaining), localTime));
    }

    private void DrawDistribution(RefreshQueueStatsSnapshot snap)
    {
        ImGui.TextDisabled(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.heading.by_priority"));
        var priorityRows = Enum.GetValues<RefreshPriority>()
            .Select(p => (
                Label: p.ToString(),
                Count: snap.RowsByPriority.TryGetValue((int)p, out var n) ? n : 0))
            .ToArray();
        NexusTable.Draw(
            "##nxk_queuestats_by_priority",
            new[]
            {
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.priority")),
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.count"), Width: 100f),
            },
            priorityRows,
            row =>
            {
                ImGui.TableNextColumn();
                NexusTable.CellText(row.Label);
                ImGui.TableNextColumn();
                NexusTable.CellText(row.Count.ToString("N0", CultureInfo.CurrentCulture),
                    ImGuiColors.DalamudGrey);
            });

        ImGui.Spacing();
        ImGui.TextDisabled(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.heading.by_category"));
        // Reuses the same resx keys as the per-category checkbox labels so
        // the table and the toggles say the same word for the same category.
        // Disabled categories are hidden — the worker won't process them
        // anyway and they'd just add zero-rows. Sort by queue count
        // descending so backlogs float to the top.
        var categoryRows = Enum.GetValues<RefreshCategory>()
            .Where(c => mPolicy.IsEnabled(c))
            .Select(c => (
                Label: mLoc.Get("nexuskit.modules.playerenrichment.refresh.category."
                                + c.ToString().ToLowerInvariant()),
                Count: snap.RowsByCategory.TryGetValue((int)c, out var n) ? n : 0))
            .OrderByDescending(r => r.Count)
            .ToArray();
        NexusTable.Draw(
            "##nxk_queuestats_by_category",
            new[]
            {
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.category")),
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.count"), Width: 100f),
            },
            categoryRows,
            row =>
            {
                ImGui.TableNextColumn();
                NexusTable.CellText(row.Label);
                ImGui.TableNextColumn();
                NexusTable.CellText(row.Count.ToString("N0", CultureInfo.CurrentCulture),
                    ImGuiColors.DalamudGrey);
            });
    }

    private void DrawTopContents(RefreshQueueStatsSnapshot snap)
    {
        ImGui.TextDisabled(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.heading.top_contents"));
        if (snap.TopContents.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3,
                mLoc.Get("nexuskit.modules.playerenrichment.queuestats.top.empty"));
            return;
        }

        NexusTable.Draw(
            "##nxk_queuestats_top",
            new[]
            {
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.name")),
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.rows"), Width: 70f),
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.max_attempts"), Width: 90f),
                new NexusTableColumn(mLoc.Get("nexuskit.modules.playerenrichment.queuestats.col.schedule"), Width: 160f),
            },
            snap.TopContents,
            row =>
            {
                var atCap = row.MaxAttemptCount >= PlayerRefreshQueueService.MaxAttempts;

                ImGui.TableNextColumn();
                NexusTable.CellText(row.Name ?? mLoc.Get("nexuskit.modules.playerenrichment.queuestats.unknown_name"),
                    row.Name is null ? ImGuiColors.DalamudGrey3 : (System.Numerics.Vector4?)null);
                ImGui.TableNextColumn();
                NexusTable.CellText(row.Rows.ToString("N0", CultureInfo.CurrentCulture),
                    ImGuiColors.DalamudGrey);
                ImGui.TableNextColumn();
                // "x/max" so the cap is visible — a 10/10 row makes it
                // obvious that the worker has given up on this entry; the
                // user can click Refresh on the player to revive it (see
                // UpsertAsync's same-priority Immediate branch).
                NexusTable.CellText(
                    string.Format(CultureInfo.CurrentCulture, "{0:N0}/{1:N0}",
                        row.MaxAttemptCount, PlayerRefreshQueueService.MaxAttempts),
                    atCap ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey);
                ImGui.TableNextColumn();
                // Schedule column is state-dependent: at-cap rows are headed
                // for the 24h cleanup, everything else is waiting on the
                // next retry. Countdown is computed per frame off
                // DateTime.UtcNow so it ticks between the 3-second snapshot
                // refreshes (DurationFormat.TwoUnit clamps negatives to 0s
                // until the next snapshot lands). Falls back to a "pending"
                // label when no concrete time exists (fresh row with no
                // failure yet, or no row past the cap yet).
                DrawScheduleCell(row, atCap);
            });
    }

    private void DrawScheduleCell(RefreshQueueTopContent row, bool atCap)
    {
        if (atCap)
        {
            var text = row.EarliestDeletionAtUtc is { } d
                ? string.Format(CultureInfo.CurrentCulture,
                    mLoc.Get("nexuskit.modules.playerenrichment.queuestats.schedule.delete_in"),
                    DurationFormat.TwoUnit(d - DateTime.UtcNow))
                : mLoc.Get("nexuskit.modules.playerenrichment.queuestats.schedule.delete_pending");
            NexusTable.CellText(text, ImGuiColors.DalamudYellow);
        }
        else
        {
            var text = row.EarliestNextRetryAtUtc is { } r
                ? string.Format(CultureInfo.CurrentCulture,
                    mLoc.Get("nexuskit.modules.playerenrichment.queuestats.schedule.retry_in"),
                    DurationFormat.TwoUnit(r - DateTime.UtcNow))
                : mLoc.Get("nexuskit.modules.playerenrichment.queuestats.schedule.retry_now");
            NexusTable.CellText(text, ImGuiColors.DalamudGrey);
        }
    }

    private void StartLoad()
    {
        if (mLoadTask is not null) return;
        // Keep the existing snapshot visible during the background reload so
        // the section doesn't flash back to the "Loading..." placeholder on
        // every auto-refresh. Only the very first load (mSnapshot already
        // null) shows the placeholder.
        mLoadTask = Task.Run(async () =>
        {
            try
            {
                var snap = await mStats.GatherAsync().ConfigureAwait(false);
                mSnapshot = snap;
                mLastLoadAt = DateTime.UtcNow;
            }
            finally { mLoadTask = null; }
        });
    }
}
