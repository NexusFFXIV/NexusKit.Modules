# Transport (NexusKit.Modules.Sync)

How `RestSyncProtocol` turns the four operations into HTTP, and how failures come back.

## Shape

`RestSyncProtocol` implements `ISyncProtocol` over `HttpClient`. It is **stateless beyond its
configuration**, so a single instance is safe to share across a plugin that drains an outbox
and refreshes a mirror at the same time.

It holds no session. Every call presents the API key; the optional `SessionToken` from the
handshake is ignored. Caching one would buy a little bandwidth and cost invalidation logic, and
the protocol is explicitly written so that a client which ignores it stays correct.

Routes are never built here — they come from `SyncRoutes` in `NexusKit.Sync`, so client and
server derive them from the same code rather than from two string literals that agree until one
is edited.

## Authentication per request, not per client

The bearer header is attached when the request is built, not baked into
`HttpClient.DefaultRequestHeaders`. That matters because the key can change at runtime: a user
pastes a new one into the settings after rotating it, and the next request has to use it
without anything being re-created.

`DescribeAsync` is sent deliberately **without** a key. A contract document describes shapes,
not data, and requiring authentication to read one would stop an author checking compatibility
against a server they have not signed up to yet.

## Errors

A non-success response becomes a `SyncProtocolException` carrying a `SyncProblem`. Transport
faults — DNS, TLS, a dropped connection — surface as their own exception types.

That distinction is for the caller's benefit: a transport fault is worth retrying, whereas most
protocol problems will produce the identical answer next time and retrying only burns the rate
limit. `SyncProtocolException.IsTransient` marks the exceptions to that rule.

### Reading Problem Details defensively

`ProblemDetailsReader` assumes the body might not be from a NexusSyncServer at all.

A failure response is exactly the moment when something else may be answering: a reverse proxy
returning its own 502 page, a captive portal, a misconfigured host serving HTML. So the reader
only attempts JSON when the content type claims it, and falls back to a status-derived problem
when parsing fails. Throwing a JSON parse error there would replace a useful message —
"502 BadGateway" — with a misleading one about malformed JSON.

Known RFC 9457 members are read into `SyncProblem`; everything else is flattened into
`Extensions` as strings. That is what lets a client surface "the server speaks 1.0 and 1.1"
without this layer having to model every problem type.

## Two small robustness details

**`User-Agent` is sanitised.** The header must parse as a product token, and a stray space in a
caller-supplied `ClientAgent` would throw inside `ParseAdd` — at request time, which is a
confusing place to discover a configuration typo. Whitespace becomes `-`.

**An empty success body is treated as a protocol problem.** A conforming server never returns
one; a proxy answering 204 for something it did not understand does. Surfacing it as
`SyncProtocolException` keeps the caller's error handling in one place instead of adding a null
check at every call site.

## Injected `HttpClient`

The constructor only fills in `BaseAddress` and `Timeout` if they are still at their defaults.
An `HttpClient` handed over by `IHttpClientFactory` may legitimately arrive pre-configured —
with a handler chain, a proxy, a policy — and overwriting that would make the factory
registration a lie.

## Testing against it

`ISyncProtocol` is an interface, so a plugin test substitutes a fake and needs no server at
all. For testing the transport itself, `RestSyncProtocolTests` in
`localTools/tests/NexusKit.Modules.Sync.Tests` drives it through a stub `HttpMessageHandler` —
covering the key header, the unauthenticated describe, per-record push outcomes, cursor and
tombstone handling, typed problems, and the non-JSON error page.

Those tests run in no CI (`NexusKit.Modules` builds but does not test), so run them by hand
when touching this package:

```powershell
dotnet test localTools\tests\NexusKit.Modules.Sync.Tests -c Debug -p:Platform=x64
```
