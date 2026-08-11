# Connections (NexusKit.Modules.Sync)

How a plugin reaches one server — or several.

## Several is the normal case

Registration is **keyed**, not singular:

```csharp
// your own server
services.AddNexusKitSync("myplugin.tracker", o =>
{
    o.ServerUrl   = new Uri("https://sync.myplugin.dev/");
    o.ApiKey      = settings.OwnApiKey;
    o.ClientAgent = "MyPlugin/0.3.0";
});

// somebody else's, reached through the client bindings they published
services.AddNexusKitSync("acme.venues", o =>
{
    o.ServerUrl   = new Uri("https://sync.acme.dev/");
    o.ApiKey      = settings.AcmeApiKey;
    o.ClientAgent = "MyPlugin/0.3.0";
});
```

```csharp
public sealed class VenueService(
    [FromKeyedServices("acme.venues")] ISyncProtocol sync) { … }
```

Using the contract id as the key keeps registration and resolution obviously in step.

This is not speculative generality. Each author runs their own server, so the moment anyone
publishes their client binding as a package, a plugin consuming it talks to that author's
server *and* to its own. Retrofitting multi-connection later would mean partitioning the
outbox, the cursors, the settings section and the key storage after the fact — cheap now,
expensive then.

Every connection is fully independent: its own `HttpClient`, address, key, agent and timeout.
An unreachable server degrades exactly one feature.

For the genuinely single-server case there is an unkeyed overload that registers
`ISyncProtocol` directly.

## Options

| Option | Notes |
|---|---|
| `ServerUrl` | Required, absolute. |
| `ApiKey` | `nxs_…`. Null is legal and means unauthenticated — enough for `DescribeAsync` and nothing else. |
| `ClientAgent` | Ends up in the server's audit log, which is what makes "one build is hammering the API" answerable. |
| `Timeout` | Per request. |
| `AllowInsecureTransport` | See below. |

`Validate()` runs eagerly at construction, and its failures name the connection key — with
several registered, "ServerUrl is required" on its own does not say which one.

## Plain HTTP is refused, not upgraded

An `http://` address throws unless `AllowInsecureTransport` is set.

An API key is a bearer credential: over plain HTTP, everyone on the path has it. Refusing is
deliberate in both directions — silently rewriting the address to `https://` would hide a
misconfiguration that matters, and an upgrade that then fails leaves the caller guessing why.

The escape hatch exists so a developer can talk to a container on localhost. That is its whole
purpose.

## Where the key belongs

**Not in the ordinary settings table.** A key sitting next to harmless options ends up in every
config export and every support screenshot. The intended handling, which the full module will
implement:

- a separate store, excluded from settings export
- a password field with a reveal toggle in the UI
- DPAPI encryption (`ProtectedData`, CurrentUser scope)
- validation on entry via a probe handshake, with the result shown in the settings

Anywhere a key might be written down — a log line, an exception, a diagnostic dump — use
`ApiKeyFormat.Redact`.

## What is not here yet

Today this package is transport only: it speaks the four operations and hands the results back.
The full module adds the outbox, the downlink mirror, cursor persistence and the background
drainer — which is what turns `PushAsync` into fire-and-forget and `GetAsync` into a local,
offline-capable read.

Until then a caller holds `ISyncProtocol` and drives it directly.
