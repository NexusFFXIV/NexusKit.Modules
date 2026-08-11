using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// Everything one connection needs to reach one server.
/// <para>One instance describes one server. A plugin talking to several — its own, plus those
/// of authors who publish their client bindings as packages — holds several of these, each
/// with its own address and its own key. That separation is what keeps one unreachable server
/// from affecting the others.</para>
/// </summary>
public sealed class SyncConnectionOptions
{
    /// <summary>Root address of the server, e.g. <c>https://sync.example.org/</c>.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>
    /// The API key, in <c>nxs_…</c> form. Null means unauthenticated, which is enough for
    /// <see cref="ISyncProtocol.DescribeAsync"/> and nothing else.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How this client identifies itself, e.g. <c>MyPlugin/0.3.0</c>. Lands in the
    /// server's audit log, which is what makes "one build is hammering the API" answerable.
    /// </summary>
    public string ClientAgent { get; set; } = "NexusKit.Modules.Sync";

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Permits a plain <c>http://</c> address.
    /// <para>Off by default and deliberately awkward to turn on. An API key is a bearer
    /// credential: over plain HTTP, everyone on the path gets it. This exists so a developer
    /// can talk to a container on localhost, and for no other reason.</para>
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>Throws when the options cannot produce a working connection.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing or unusable.</exception>
    public void Validate()
    {
        if (ServerUrl is null)
            throw new InvalidOperationException($"{nameof(ServerUrl)} is required.");

        if (!ServerUrl.IsAbsoluteUri)
            throw new InvalidOperationException($"{nameof(ServerUrl)} must be an absolute URI, got '{ServerUrl}'.");

        var https = string.Equals(ServerUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var http = string.Equals(ServerUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!https && !http)
            throw new InvalidOperationException($"{nameof(ServerUrl)} must be http or https, got '{ServerUrl.Scheme}'.");

        if (http && !AllowInsecureTransport)
        {
            // Refused rather than upgraded. Silently rewriting the address would hide a
            // misconfiguration that matters, and an upgrade that fails leaves the caller
            // guessing why.
            throw new InvalidOperationException(
                $"{nameof(ServerUrl)} is plain HTTP ('{ServerUrl}'), which would put the API key on the wire "
                + $"in the clear. Use https, or set {nameof(AllowInsecureTransport)} for local development.");
        }

        if (ApiKey is not null && !ApiKeyFormat.IsWellFormed(ApiKey))
        {
            throw new InvalidOperationException(
                $"{nameof(ApiKey)} '{ApiKeyFormat.Redact(ApiKey)}' is not shaped like a sync API key "
                + $"({ApiKeyFormat.Prefix} followed by {ApiKeyFormat.BodyLength} characters).");
        }

        if (string.IsNullOrWhiteSpace(ClientAgent))
            throw new InvalidOperationException($"{nameof(ClientAgent)} is required.");

        if (Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"{nameof(Timeout)} must be positive.");
    }
}
