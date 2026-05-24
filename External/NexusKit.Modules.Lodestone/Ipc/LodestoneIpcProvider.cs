using System.Text.Json;
using NexusKit.Core.Ipc;
using NexusKit.Modules.Lodestone.Clients;

namespace NexusKit.Modules.Lodestone.Ipc;

/// <summary>
/// Publishes Lodestone endpoints as IPCs (JSON-serialised responses).
/// <para>
/// Full IPC names (assuming plugin "PlayerNexusTracker"):
/// <list type="bullet">
/// <item><c>PlayerNexusTracker.Lodestone.GetCharacterJson</c></item>
/// <item><c>PlayerNexusTracker.Lodestone.SearchCharacterJson</c></item>
/// </list>
/// </para>
/// </summary>
internal sealed class LodestoneIpcProvider : IIpcProvider, IDisposable
{
    private const string Subsystem = "Lodestone";

    private readonly List<IDisposable> mRegistrations = new();

    public LodestoneIpcProvider(IIpcRegistry ipc, ILodestoneClient client)
    {
        mRegistrations.Add(ipc.RegisterFunc<ulong, Task<string?>>(
            Subsystem, "GetCharacterJson",
            async id => SerializeOrNull(await client.GetCharacterAsync(id).ConfigureAwait(false))));

        mRegistrations.Add(ipc.RegisterFunc<string, string, Task<string?>>(
            Subsystem, "SearchCharacterJson",
            async (name, world) => SerializeOrNull(await client.SearchCharacterAsync(name, world).ConfigureAwait(false))));
    }

    private static string? SerializeOrNull<T>(T? value) where T : class
        => value is null ? null : JsonSerializer.Serialize(value);

    public void Dispose()
    {
        foreach (var r in mRegistrations) r.Dispose();
        mRegistrations.Clear();
    }
}
