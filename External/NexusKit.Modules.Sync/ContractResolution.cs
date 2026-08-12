using NexusKit.Sync.Contracts;
using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// Decides which contract document a client works from: the server's, or its own.
/// </summary>
public static class ContractResolution
{
    /// <summary>
    /// Picks the highest version the server offers that this client can actually speak, and stays
    /// where it is when none of them will do.
    /// <para><b>The server leads, but it does not drag.</b> A key carrying the built-in
    /// contract-reading scope is a statement that this client may follow the server's schema — not
    /// that it can follow it anywhere. Taking the newest document unread works right up until the
    /// version that removes a field this client reads, at which point the client has agreed to a
    /// document it cannot honour. So each offered version is measured against what this client was
    /// built for, newest first, and the first one it can carry wins.</para>
    /// <para>Falling behind is a normal outcome, not a failure: the previous version keeps working,
    /// which is the entire reason old minors stay registered. <see cref="ResolvedContract.Blockers"/>
    /// says what it cost. <b>Callers are expected to surface that</b> — this returns the reason
    /// rather than logging it, because the client owns the log and a silent downgrade is the one
    /// outcome nobody can diagnose later.</para>
    /// <para>Without the scope the server does not hand out documents at all, and the client must
    /// already know the contract. That is the deliberate posture: a server should not describe what
    /// it holds to anyone who asks.</para>
    /// </summary>
    /// <param name="protocol">The connection to ask.</param>
    /// <param name="local">
    /// What the client shipped with, and the yardstick every offered version is held against. Used
    /// as-is when the server refuses; may be null, in which case a refusal is fatal — there would be
    /// nothing left to talk about — and there is nothing to preserve, so the server's version is
    /// taken unexamined.
    /// </param>
    /// <param name="contractId">Which contract, when <paramref name="local"/> is null.</param>
    /// <param name="version">
    /// Which version to ask for. Its <c>major</c> bounds the search: crossing a major needs a
    /// rebuilt client, not a negotiation.
    /// </param>
    /// <param name="grantedScopes">
    /// The client's bare scopes from the handshake, deciding which collections it reads and which it
    /// writes — and therefore what can break it. Null falls back to every scope
    /// <paramref name="local"/> declares, which is safe but pessimistic: the client is then held
    /// back over collections its key may not even touch. Prefer handshaking first and passing the
    /// real set.
    /// </param>
    /// <param name="ct">Cancels the lookup.</param>
    /// <exception cref="SyncProtocolException">
    /// The server refused and there is no local document to fall back on.
    /// </exception>
    public static async Task<ResolvedContract> ResolveAsync(
        ISyncProtocol protocol,
        SyncContract? local,
        string contractId,
        ContractVersion version,
        IReadOnlySet<string>? grantedScopes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        var id = local?.ContractId ?? contractId;

        try
        {
            var descriptor = await protocol
                .DescribeAsync(new ContractRef(id, version), ct)
                .ConfigureAwait(false);

            // Nothing to preserve, so nothing to weigh. A client with no document of its own has
            // no expectations that the newest version could violate.
            if (local is null)
                return new ResolvedContract(descriptor.ToContract(), FromServer: true, descriptor.Version, []);

            var scopes = grantedScopes ?? new HashSet<string>(ContractScopes.All(local), StringComparer.Ordinal);

            // Descending, so the search stops at the first version that fits rather than examining
            // every one. The opening request bought the list of versions and the document for the
            // one we asked about — Describe returns the exact version requested, it does not
            // negotiate — so a client already on the newest pays one request and a client that has
            // to move pays two.
            var candidates = descriptor.AvailableVersions
                .Where(v => v.Major == version.Major)
                .OrderByDescending(v => v)
                .ToArray();

            ContractVersion? highest = candidates.Length > 0 ? candidates[0] : null;
            var rejected = new List<string>();

            foreach (var candidate in candidates)
            {
                var document = candidate == descriptor.Version
                    ? descriptor.ToContract()
                    : (await protocol.DescribeAsync(new ContractRef(id, candidate), ct).ConfigureAwait(false))
                        .ToContract();

                var support = ClientSupport.Evaluate(local, document, scopes);

                if (support.IsSupported)
                    return new ResolvedContract(document, FromServer: true, highest, rejected);

                // Prefixed, because "field 'source_id' is required" without a version is a puzzle
                // and "1.2: field 'source_id' is required" is the answer to "why am I still on 1.1".
                foreach (var blocker in support.Blockers) rejected.Add($"{candidate}: {blocker}");
            }

            // Every offered version costs this client something, so it keeps what it has. Its own
            // document still matches whatever minor the handshake settles on, because old minors
            // stay registered.
            return new ResolvedContract(local, FromServer: false, highest, rejected);
        }
        catch (SyncProtocolException ex)
            when (ex.Problem.Type is SyncProblemType.ScopeMissing or SyncProblemType.Unauthenticated)
        {
            // Not an error: this key is not permitted to read documents, which is the mode
            // where the client is expected to carry its own. No reading means no choosing.
            if (local is null) throw;

            return new ResolvedContract(local, FromServer: false, HighestOffered: null, []);
        }
    }
}

/// <summary>
/// The contract to work from, where it came from, and what choosing it cost.
/// </summary>
/// <param name="Contract">The document both sides will be held to.</param>
/// <param name="FromServer">
/// True when the server supplied it. Worth surfacing: it is the difference between "we agree
/// because I checked" and "we agree as far as I know".
/// </param>
/// <param name="HighestOffered">
/// The newest version the server has for this major, or null when it offered none — either because
/// it holds no version of this major or because it would not say. Compare against
/// <paramref name="Contract"/>'s version to see whether this client is current.
/// </param>
/// <param name="Blockers">
/// Why the newer versions were passed over, each prefixed with the version it refers to. Empty when
/// the client is on the newest, and the thing to log when it is not.
/// </param>
public sealed record ResolvedContract(
    SyncContract Contract,
    bool FromServer,
    ContractVersion? HighestOffered,
    IReadOnlyList<string> Blockers)
{
    /// <summary>
    /// True when a newer version exists that this client could not take. The condition worth a log
    /// line: being behind is fine, being behind without anyone knowing why is not.
    /// </summary>
    public bool IsBehind => HighestOffered is { } highest && highest > Contract.Version;
}
