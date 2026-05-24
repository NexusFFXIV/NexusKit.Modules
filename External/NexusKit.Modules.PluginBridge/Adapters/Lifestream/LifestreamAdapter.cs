using System.Numerics;
using Microsoft.Extensions.Logging;
using NexusKit.Core;
using NexusKit.Core.Actions;
using NexusKit.Core.Ipc;
using NexusKit.GameData;

namespace NexusKit.Modules.PluginBridge.Adapters.Lifestream;

internal sealed class LifestreamAdapter : IExternalPluginAdapter, ILifestreamAdapter
{
    // How long a failed TryInvoke keeps the adapter marked unavailable
    // even if Dalamud still reports the plugin as loaded. Dalamud's
    // InstalledPlugins.IsLoaded lags during plugin unload: the IPC
    // subscriber tears down first, IsLoaded flips false several seconds
    // later. Without a grace window the UI would keep offering the
    // action between failure and the IsLoaded flip. The window is
    // self-clearing once Dalamud agrees the plugin is gone (see Probe),
    // so a Lifestream reload comes back online immediately, not after
    // the timeout.
    private static readonly TimeSpan IpcFailureGrace = TimeSpan.FromSeconds(60);

    private readonly IDalamudPluginProbe mProbe;
    private readonly IIpcRegistry mIpc;
    private readonly IGameDataLookups mLookups;
    private readonly ILogger<LifestreamAdapter> mLog;
    private readonly IIpcAction<string> mExecuteCommand;

    private ExternalPluginAdapterStatus mStatus;
    private DateTime mLastIpcFailureUtc = DateTime.MinValue;

    public LifestreamAdapter(
        IDalamudPluginProbe probe,
        IIpcRegistry ipc,
        IGameDataLookups lookups,
        ILogger<LifestreamAdapter> log)
    {
        mProbe = probe;
        mIpc = ipc;
        mLookups = lookups;
        mLog = log;
        mExecuteCommand = mIpc.GetAction<string>(LifestreamIpcNames.ExecuteCommand);
        mStatus = Probe();
    }

    public string AdapterKey => "Lifestream";

    public string PluginInternalName => LifestreamIpcNames.PluginInternalName;

    public string DisplayNameKey => "nexuskit.bridges.lifestream.display_name";

    public string DescriptionKey => "nexuskit.bridges.lifestream.description";

    public bool IsAvailable
    {
        get
        {
            var s = GetStatus();
            return s.PluginLoaded && s.AllRequiredIpcsAvailable;
        }
    }

    public ExternalPluginAdapterStatus GetStatus()
    {
        // Re-probe on every read. Probe is in-memory (one InstalledPlugins
        // scan), so calling it inline is cheap and closes the staleness
        // window between background self-check ticks — a user who disables
        // Lifestream and immediately clicks the FC-house button gets the
        // correct "not available" verdict on the next IsAvailable read,
        // not 30 s later. mStatus is still maintained so the periodic
        // self-check loop has something to update and any direct-field
        // read (unlikely, but possible from future code) sees a recent
        // snapshot.
        mStatus = Probe();
        return mStatus;
    }

    public void Refresh()
    {
        mStatus = Probe();
    }

    public bool TryExecuteCommand(string commandArguments)
    {
        if (!IsAvailable)
        {
            mLog.LogDebug("Lifestream not available; skipping ExecuteCommand({Args})", commandArguments);
            return false;
        }

        if (!mExecuteCommand.TryInvoke(commandArguments))
        {
            mLog.LogWarning("Lifestream.ExecuteCommand failed for arguments {Args}", commandArguments);
            // Live invocation just failed, so the IPC subscriber is gone
            // even if Dalamud's InstalledPlugins.IsLoaded still claims
            // otherwise. Stamp the failure + refresh so the very next
            // IsAvailable read sees the downgrade — that makes any UI
            // gated on IsAvailable (e.g. the FC-housing button) hide on
            // the next frame instead of waiting for the next 30 s tick.
            mLastIpcFailureUtc = DateTime.UtcNow;
            Refresh();
            return false;
        }

        return true;
    }

    public bool TryGotoFreeCompanyHouse(PlayerLocation? currentLocation, IHousingAddress address)
    {
        if (!IsAvailable)
        {
            mLog.LogDebug("Lifestream not available; skipping GotoFreeCompanyHouse");
            return false;
        }

        // Three command shapes based on the current vs target geography:
        //   1) Cross-DC  → just the target DC's English name (no other args).
        //                  Lifestream travels the DC; the user clicks the
        //                  button again after arriving and we land in case 2/3.
        //   2) Same DC, different world → "<targetWorld> <district> <ward> <plot>"
        //                  (Lifestream does a world visit + housing leg).
        //   3) Same world (or location unknown) → "<district> <ward> <plot>".
        // DecideTravelVariant + BuildTravelCommand centralise both the choice
        // and the command construction so PreviewGotoFreeCompanyHouse can show
        // the user the exact same string we're about to dispatch.
        var variant = DecideTravelVariant(currentLocation, address);
        if (variant is null)
        {
            mLog.LogDebug(
                "Address missing district/ward/plot or non-residential; skipping (district={D}, ward={W}, plot={P})",
                address.DistrictTerritoryId, address.Ward, address.PlotNumber);
            return false;
        }

        var command = BuildTravelCommand(variant.Value, address, logWarnings: true);
        if (command is null)
        {
            // BuildTravelCommand already logged the specific failure (no DC
            // name resolved / no English district name) — nothing more to
            // add here.
            return false;
        }
        return TryExecuteCommand(command);
    }

    public ActionRenderHint? PreviewGotoFreeCompanyHouse(
        PlayerLocation? currentLocation, IHousingAddress address)
    {
        // Mirror TryGoto's early-exit conditions so a non-null hint is the
        // UI's single predicate for "show the button". When Lifestream is
        // unavailable or the address can't be acted on, we return null and
        // the caller suppresses the button entirely.
        if (!IsAvailable) return null;
        var variant = DecideTravelVariant(currentLocation, address);
        if (variant is null) return null;

        // Compute the actual /li payload so the tooltip can show it on a
        // second line — same builder TryGoto uses, so what the user reads
        // in the tooltip is exactly what we'd dispatch on click.
        // logWarnings: false because this method runs per frame (DrawEstate
        // is called every render tick); the warning fires from the click
        // path instead.
        var command = BuildTravelCommand(variant.Value, address, logWarnings: false);
        var detail = command is not null ? $"/li {command}" : null;

        // Colors are intentionally semantic, not branded — they signal effort,
        // not state. Same-world keeps the default button background (no tint)
        // because it's the "just do it" case. Cyan marks the world-visit leg
        // (medium effort: visit + housing). Amber marks the cross-DC start of
        // a multi-step journey so the user notices they're committing to more
        // than a single teleport.
        return variant switch
        {
            FcHouseTravelVariant.SameWorld => new ActionRenderHint(
                Variant: "lifestream.fc_house.same_world",
                TooltipKey: "nexuskit.bridges.lifestream.travel.tooltip.same_world",
                DetailText: detail),
            FcHouseTravelVariant.WorldVisit => new ActionRenderHint(
                Variant: "lifestream.fc_house.world_visit",
                TooltipKey: "nexuskit.bridges.lifestream.travel.tooltip.world_visit",
                AccentColor: new Vector4(0.30f, 0.60f, 0.90f, 1f),
                DetailText: detail),
            FcHouseTravelVariant.CrossDc => new ActionRenderHint(
                Variant: "lifestream.fc_house.cross_dc",
                TooltipKey: "nexuskit.bridges.lifestream.travel.tooltip.cross_dc",
                AccentColor: new Vector4(0.85f, 0.55f, 0.20f, 1f),
                DetailText: detail),
            _ => null,
        };
    }

    /// <summary>Builds the literal <c>/li</c> argument string for a travel
    /// variant. Shared between <see cref="TryGotoFreeCompanyHouse"/> (which
    /// dispatches it) and <see cref="PreviewGotoFreeCompanyHouse"/> (which
    /// surfaces it in the tooltip) so the two cannot drift. Returns
    /// <c>null</c> on a required-lookup failure (no English district name,
    /// no DC name resolved). The <paramref name="logWarnings"/> flag exists
    /// because Preview is called per UI frame — warnings would flood the
    /// log; the click path passes <c>true</c> so failures stay visible.</summary>
    private string? BuildTravelCommand(
        FcHouseTravelVariant variant, IHousingAddress address, bool logWarnings)
    {
        if (variant == FcHouseTravelVariant.CrossDc)
        {
            if (address.WorldId is not { } twid) return null;
            var targetDc = mLookups.GetDataCenterIdByWorldId(twid);
            if (targetDc is null)
            {
                if (logWarnings)
                    mLog.LogWarning("No data center resolved for target world {Id}", twid);
                return null;
            }
            var dcName = mLookups.GetDataCenterName(targetDc.Value);
            if (string.IsNullOrWhiteSpace(dcName))
            {
                if (logWarnings)
                    mLog.LogWarning("No name resolved for target data center {Id}", targetDc);
                return null;
            }
            return dcName;
        }

        // Variants 2 + 3 share the "<[world] district ward plot>" command
        // shape. The english PlaceName ("Mist", "The Lavender Beds",
        // "Shirogane", …) is what Lifestream's command parser matches against
        // — its ParseResidentialAetheryteKind does a case-insensitive
        // substring lookup, so multi-word names with leading articles still
        // resolve.
        var districtId = address.DistrictTerritoryId!.Value;
        var districtEn = mLookups.GetTerritoryName(
            (ushort)districtId, GameDataClientLanguage.English);
        if (string.IsNullOrWhiteSpace(districtEn))
        {
            if (logWarnings)
                mLog.LogWarning(
                    "GameData resolver returned no English name for TerritoryType {Id}", districtId);
            return null;
        }

        var prefix = string.Empty;
        if (variant == FcHouseTravelVariant.WorldVisit && address.WorldId is { } wid)
        {
            var worldName = mLookups.GetWorldName(wid);
            if (!string.IsNullOrWhiteSpace(worldName))
                prefix = worldName + " ";
            else if (logWarnings)
                mLog.LogWarning(
                    "No English name resolved for target world {Id}; sending no world prefix", wid);
        }

        return $"{prefix}{districtEn} {address.Ward!.Value} {address.PlotNumber!.Value}";
    }

    private FcHouseTravelVariant? DecideTravelVariant(
        PlayerLocation? currentLocation, IHousingAddress address)
    {
        if (address.DistrictTerritoryId is not { } districtId
            || address.Ward is null
            || address.PlotNumber is null)
            return null;
        if (!ResidentialDistricts.IsResidential(districtId))
            return null;

        var currentDc = currentLocation?.WorldId is { } cw
            ? mLookups.GetDataCenterIdByWorldId(cw)
            : null;
        var targetDc = address.WorldId is { } tw
            ? mLookups.GetDataCenterIdByWorldId(tw)
            : null;

        if (currentDc is { } cdc && targetDc is { } tdc && cdc != tdc)
            return FcHouseTravelVariant.CrossDc;

        if (address.WorldId is { } twid
            && currentLocation?.WorldId is { } cwid
            && twid != cwid)
            return FcHouseTravelVariant.WorldVisit;

        return FcHouseTravelVariant.SameWorld;
    }

    private enum FcHouseTravelVariant
    { SameWorld, WorldVisit, CrossDc }

    private ExternalPluginAdapterStatus Probe()
    {
        var info = mProbe.GetInfo(LifestreamIpcNames.PluginInternalName);
        var installed = info is not null;
        var loaded = info?.IsLoaded ?? false;
        var version = info?.Version;

        // Dalamud cannot prove an IPC will invoke without side-effects, so we
        // treat "plugin loaded" as optimistic evidence — and downgrade it
        // when a live TryInvoke recently failed (see TryExecuteCommand). The
        // grace stamp is self-clearing: once Dalamud agrees the plugin is
        // gone (IsLoaded == false), we wipe it so a later reload starts
        // from a clean state instead of being blocked for the rest of the
        // window.
        if (!loaded)
            mLastIpcFailureUtc = DateTime.MinValue;
        var recentFailure = DateTime.UtcNow - mLastIpcFailureUtc < IpcFailureGrace;
        var allIpcsOk = loaded && !recentFailure;
        var missing = allIpcsOk
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : new[] { LifestreamIpcNames.ExecuteCommand };

        return new ExternalPluginAdapterStatus(installed, loaded, version, allIpcsOk, missing);
    }
}