using NexusKit.Sync.Contracts;
using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// Decides which contract document a client works from: the server's, or its own.
/// </summary>
public static class ContractResolution
{
    /// <summary>
    /// Fetches the server's contract when the key may read it, and falls back to the local
    /// document when it may not.
    /// <para><b>The server's copy wins.</b> A key carrying the built-in contract-reading scope
    /// is a statement that this client is allowed to follow the server's schema, and the
    /// server is where a contract is registered — so its version is authoritative, and a
    /// local copy that has drifted stops being a problem to diagnose.</para>
    /// <para>Without that scope the server does not hand out documents at all, and the client
    /// must already know the contract. That is the deliberate posture: a server should not
    /// describe what it holds to anyone who asks.</para>
    /// </summary>
    /// <param name="protocol">The connection to ask.</param>
    /// <param name="local">
    /// What the client shipped with. Used when the server refuses; may be null, in which case
    /// a refusal is fatal — there would be nothing left to talk about.
    /// </param>
    /// <param name="contractId">Which contract, when <paramref name="local"/> is null.</param>
    /// <param name="version">Which version to ask for.</param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <exception cref="SyncProtocolException">
    /// The server refused and there is no local document to fall back on.
    /// </exception>
    public static async Task<ResolvedContract> ResolveAsync(
        ISyncProtocol protocol,
        SyncContract? local,
        string contractId,
        ContractVersion version,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        try
        {
            var descriptor = await protocol
                .DescribeAsync(new ContractRef(local?.ContractId ?? contractId, version), ct)
                .ConfigureAwait(false);

            return new ResolvedContract(ContractJson.Parse(descriptor.CanonicalJson), FromServer: true);
        }
        catch (SyncProtocolException ex)
            when (ex.Problem.Type is SyncProblemType.ScopeMissing or SyncProblemType.Unauthenticated)
        {
            // Not an error: this key is not permitted to read documents, which is the mode
            // where the client is expected to carry its own.
            if (local is null) throw;

            return new ResolvedContract(local, FromServer: false);
        }
    }
}

/// <summary>
/// The contract to work from, and where it came from.
/// </summary>
/// <param name="Contract">The document both sides will be held to.</param>
/// <param name="FromServer">
/// True when the server supplied it. Worth surfacing: it is the difference between "we agree
/// because I checked" and "we agree as far as I know".
/// </param>
public sealed record ResolvedContract(SyncContract Contract, bool FromServer);
