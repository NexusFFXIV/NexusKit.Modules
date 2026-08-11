using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// The REST implementation of <see cref="ISyncProtocol"/>.
/// <para>Stateless beyond its configuration, so it is safe to share across a plugin that
/// drains an outbox and refreshes a mirror at the same time. It holds no session: every call
/// presents the API key. The handshake may return a session token and this client ignores it —
/// caching one would buy a little bandwidth and cost invalidation logic, and the protocol is
/// explicitly written so that a client which ignores it stays correct.</para>
/// </summary>
public sealed class RestSyncProtocol : ISyncProtocol
{
    private readonly HttpClient mHttp;
    private readonly SyncConnectionOptions mOptions;
    private readonly ILogger mLog;

    /// <summary>Creates the client. Validates the options eagerly.</summary>
    /// <exception cref="InvalidOperationException">The options cannot produce a working connection.</exception>
    public RestSyncProtocol(HttpClient http, SyncConnectionOptions options, ILogger<RestSyncProtocol>? log = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        mHttp = http;
        mOptions = options;
        mLog = log ?? NullLogger<RestSyncProtocol>.Instance;

        // Only set what the caller has not already configured — an HttpClient handed in by
        // IHttpClientFactory may legitimately arrive pre-configured, and stomping on that
        // would make the factory registration a lie.
        mHttp.BaseAddress ??= options.ServerUrl;
        if (mHttp.Timeout == TimeSpan.FromSeconds(100)) mHttp.Timeout = options.Timeout;
    }

    /// <inheritdoc />
    public async Task<HandshakeResult> HandshakeAsync(HandshakeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = Authenticated(HttpMethod.Post, SyncRoutes.Handshake());
        message.Content = JsonContent.Create(request, options: SyncJson.Options);

        var result = await SendAsync<HandshakeResult>(message, ct).ConfigureAwait(false);

        if (result.ServerMessage is { Length: > 0 } motd)
            mLog.LogInformation("Server notice for {Contract}: {Message}", request.ContractId, motd);

        if (!string.Equals(result.ServerContractHash, request.ContractHash, StringComparison.Ordinal))
        {
            // Not an error — the negotiated version is authoritative and a differing hash is
            // normal when the server runs a newer minor. Logged because when something *does*
            // go wrong later, this line is the difference between a diff and a mystery.
            mLog.LogDebug(
                "Contract {Contract} hashes differ (client {ClientHash}, server {ServerHash}); negotiated {Version}.",
                request.ContractId, request.ContractHash, result.ServerContractHash, result.NegotiatedVersion);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = Authenticated(HttpMethod.Post, SyncRoutes.Push(request.ContractId, request.Collection));
        message.Content = JsonContent.Create(request, options: SyncJson.Options);

        return await SendAsync<PushResult>(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PullResult> PullAsync(PullRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var route = SyncRoutes.Pull(
            request.ContractId, request.Version, request.Collection, request.Since, request.Limit);
        using var message = Authenticated(HttpMethod.Get, route);

        return await SendAsync<PullResult>(message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContractDescriptor> DescribeAsync(ContractRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // Authenticated, unlike every other read-only description you might expect. A contract
        // document is the shape of everything a server holds, and servers gate it behind the
        // built-in contract:pull scope. A key without that scope gets a scope-missing problem,
        // which is the signal to fall back on the contract the client already carries.
        using var message = Authenticated(HttpMethod.Get, SyncRoutes.Contract(reference.ContractId, reference.Version));

        return await SendAsync<ContractDescriptor>(message, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage Authenticated(HttpMethod method, string route)
    {
        var message = new HttpRequestMessage(method, route);

        if (mOptions.ApiKey is { Length: > 0 } key)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        message.Headers.UserAgent.ParseAdd(SanitizeAgent(mOptions.ClientAgent));
        return message;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken ct)
    {
        using var response = await mHttp.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ProblemDetailsReader.ReadAsync(response, ct).ConfigureAwait(false);

            mLog.LogDebug(
                "{Method} {Route} failed: {Problem}",
                message.Method, message.RequestUri, problem);

            throw new SyncProtocolException(problem);
        }

        var payload = await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, ct).ConfigureAwait(false);

        if (payload is null)
        {
            // A conforming server never does this; a proxy returning 204 for something it did
            // not understand does. Surfacing it as a protocol problem keeps the caller's
            // error handling in one place instead of adding a null check at every call site.
            throw new SyncProtocolException(new SyncProblem(
                "about:blank",
                "Empty response",
                (int)response.StatusCode,
                $"{message.Method} {message.RequestUri} succeeded but returned no body."));
        }

        return payload;
    }

    private static string SanitizeAgent(string agent)
    {
        // User-Agent has to parse as a product token; a stray space from a caller-supplied
        // string would throw inside ParseAdd rather than at configuration time, which is a
        // confusing place to discover a typo.
        Span<char> buffer = stackalloc char[agent.Length];
        var length = 0;

        foreach (var c in agent)
        {
            buffer[length++] = char.IsWhiteSpace(c) ? '-' : c;
        }

        return new string(buffer[..length]);
    }
}
