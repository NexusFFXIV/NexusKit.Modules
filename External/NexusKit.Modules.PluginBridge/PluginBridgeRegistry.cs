using Microsoft.Extensions.Logging;
using NexusKit.Core;

namespace NexusKit.Modules.PluginBridge;

internal sealed class PluginBridgeRegistry
    : IPluginBridgeRegistry, IPluginBackgroundService, IDisposable, IAsyncDisposable
{
    // How often the self-check loop re-probes adapters. Probes are in-memory
    // (one InstalledPlugins scan, no IO), so a tight cadence is cheap and
    // matters for UX: enabling Lifestream mid-session lights up the
    // FC-housing button within a few seconds instead of waiting for the
    // user to open the settings tab and click Re-check.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // Brief delay so plugin startup (host build, view rebuild, watcher
    // hydrate) settles before the first probe — the constructor itself
    // already produces an initial status, so there is no rush.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, IExternalPluginAdapter> mByKey;
    private readonly IReadOnlyList<IExternalPluginAdapter> mOrdered;
    private readonly IPluginLifetime mLifetime;
    private readonly ILogger<PluginBridgeRegistry> mLog;

    private Task? mLoopTask;
    private bool mDisposed;

    public PluginBridgeRegistry(
        IEnumerable<IExternalPluginAdapter> adapters,
        IPluginLifetime lifetime,
        ILogger<PluginBridgeRegistry> log)
    {
        var list = adapters.ToList();
        mOrdered = list;
        mByKey = list.ToDictionary(a => a.AdapterKey, StringComparer.Ordinal);
        mLifetime = lifetime;
        mLog = log;

        // Auto-start the self-check loop. Adapters cache their status; without
        // this nothing would re-probe between explicit Refresh() calls, so
        // the UI would stay stale after a user enables/disables a foreign
        // plugin mid-session. Skip the loop when there is nothing to probe.
        if (mOrdered.Count > 0)
            mLoopTask = Task.Run(() => LoopAsync(mLifetime.Stopping));
    }

    public IReadOnlyList<IExternalPluginAdapter> All() => mOrdered;

    public IExternalPluginAdapter? Get(string adapterKey)
        => mByKey.TryGetValue(adapterKey, out var a) ? a : null;

    public void RefreshAll()
    {
        foreach (var a in mOrdered)
        {
            try
            {
                a.Refresh();
            }
            catch (Exception ex)
            {
                // Per-adapter fence: a buggy Refresh in one adapter must not
                // skip the others, and must not poison the background loop.
                mLog.LogWarning(ex, "Adapter '{Key}' Refresh() threw.", a.AdapterKey);
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(InitialDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                RefreshAll();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // RefreshAll already isolates per-adapter exceptions, so this
                // catch only fires if something more fundamental went wrong
                // (e.g. the logger itself). Log + continue — the next tick
                // retries.
                mLog.LogWarning(ex, "PluginBridge self-check tick failed.");
            }

            try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (mDisposed) return;
        mDisposed = true;

        if (mLoopTask is not null)
        {
            try { await mLoopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                mLog.LogWarning(ex, "PluginBridge self-check loop terminated with an unhandled error.");
            }
        }
    }

    public void Dispose()
    {
        if (mDisposed) return;
        mDisposed = true;

        try { mLoopTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* OperationCanceledException inside */ }
        catch (Exception ex)
        {
            mLog.LogWarning(ex, "PluginBridge self-check loop terminated with an unhandled error.");
        }
    }
}
