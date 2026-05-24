using System.Text.Json;
using NexusKit.Core.Ipc;
using NexusKit.Modules.FfxivCollect.Clients;

namespace NexusKit.Modules.FfxivCollect.Ipc;

/// <summary>
/// Publishes FFXIVCollect endpoints as IPCs so foreign plugins can use them
/// without depending on our types. Each function returns the response as a
/// JSON string; consumers deserialize against their own model.
/// <para>
/// Full IPC names (assuming plugin "PlayerNexusTracker"):
/// <list type="bullet">
/// <item><c>PlayerNexusTracker.FfxivCollect.GetCharacterJson</c></item>
/// <item><c>PlayerNexusTracker.FfxivCollect.GetMountsJson</c></item>
/// <item><c>PlayerNexusTracker.FfxivCollect.GetMinionsJson</c></item>
/// <item><c>PlayerNexusTracker.FfxivCollect.GetAchievementsJson</c></item>
/// </list>
/// </para>
/// </summary>
internal sealed class FfxivCollectIpcProvider : IIpcProvider, IDisposable
{
    private const string Subsystem = "FfxivCollect";

    private readonly List<IDisposable> mRegistrations = new();

    public FfxivCollectIpcProvider(IIpcRegistry ipc, IFfxivCollectClient client)
    {
        mRegistrations.Add(ipc.RegisterFunc<ulong, Task<string?>>(
            Subsystem, "GetCharacterJson",
            async id => SerializeOrNull(await client.GetCharacterAsync(id).ConfigureAwait(false))));

        mRegistrations.Add(ipc.RegisterFunc<ulong, Task<string?>>(
            Subsystem, "GetMountsJson",
            async id => SerializeOrNull(await client.GetMountsAsync(id).ConfigureAwait(false))));

        mRegistrations.Add(ipc.RegisterFunc<ulong, Task<string?>>(
            Subsystem, "GetMinionsJson",
            async id => SerializeOrNull(await client.GetMinionsAsync(id).ConfigureAwait(false))));

        mRegistrations.Add(ipc.RegisterFunc<ulong, Task<string?>>(
            Subsystem, "GetAchievementsJson",
            async id => SerializeOrNull(await client.GetAchievementsAsync(id).ConfigureAwait(false))));
    }

    private static string? SerializeOrNull<T>(T? value) where T : class
        => value is null ? null : JsonSerializer.Serialize(value);

    public void Dispose()
    {
        foreach (var r in mRegistrations) r.Dispose();
        mRegistrations.Clear();
    }
}
