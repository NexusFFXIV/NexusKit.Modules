# NexusKit.Modules.Sync

The client half of the sync stack: the REST transport, API-key handling, and connection
registration. Talks to any server speaking the protocol.

**No Dalamud reference.** The protocol itself — contract model, canonical form, `ISyncProtocol`
— lives in [`NexusKit.Sync`](../../../NexusKit/NexusKit.Sync/README.md), which the server
references too.

## Public API

| Type | File | Purpose |
|---|---|---|
| `RestSyncProtocol` | `RestSyncProtocol.cs` | `ISyncProtocol` over `HttpClient`. Stateless beyond its configuration, safe to share concurrently. Presents the API key on every call; sends `DescribeAsync` unauthenticated by design. |
| `SyncConnectionOptions` | `SyncConnectionOptions.cs` | One connection: `ServerUrl`, `ApiKey`, `ClientAgent`, `Timeout`, `AllowInsecureTransport`. `Validate()` runs eagerly and names the connection in its failures. |
| `SyncServiceCollectionExtensions` | `SyncServiceCollectionExtensions.cs` | `AddNexusKitSync(key, configure)` for a keyed connection, `AddNexusKitSync(configure)` for a single unkeyed one. |
| `ProblemDetailsReader` | `ProblemDetailsReader.cs` | *(internal)* Maps a failure response onto `SyncProblem`, defensively — the responder might be a proxy, not a server. |

## Registration

```csharp
services.AddNexusKitSync("acme.myplugin", o =>
{
    o.ServerUrl   = new Uri("https://sync.example.org/");
    o.ApiKey      = settings.ApiKey;          // nxs_… , pasted by the user
    o.ClientAgent = "MyPlugin/1.0";
});
```

Registration is **keyed**, because talking to several servers is the normal case rather than an
exception: each author runs their own, so a plugin consuming somebody's published client
binding talks to that author's server and to its own. Each connection has its own address, key
and `HttpClient`, so one unreachable server does not affect the others.

```csharp
public sealed class ItemService(
    [FromKeyedServices("acme.myplugin")] ISyncProtocol sync) { … }
```

An unkeyed overload registers `ISyncProtocol` directly for the single-server case.

## What it does and does not do

| | |
|---|---|
| **Does** | Speaks the four protocol operations, presents the API key, maps Problem Details onto `SyncProtocolException`, refuses plain HTTP |
| **Not yet** | Outbox, downlink mirror, cursors, background draining, settings UI — what turns `PushAsync` into fire-and-forget and `GetAsync` into a local, offline-capable read |

Until then, callers hold `ISyncProtocol` and drive it themselves.

## Plain HTTP is refused, not upgraded

An API key is a bearer credential: over plain HTTP, everyone on the path has it. A `http://`
address throws at configuration time rather than being silently rewritten, because a silent
rewrite hides a misconfiguration that matters. `AllowInsecureTransport` exists so a developer
can talk to a container on localhost, and for no other reason.

## Further reading

| Document | What it covers |
|---|---|
| [docs/connections.md](docs/connections.md) | Multi-connection registration, the options, and where the API key belongs |
| [docs/transport.md](docs/transport.md) | HTTP mapping, defensive Problem Details parsing, and the robustness details behind it |

## License

**AGPL-3.0-only.**
