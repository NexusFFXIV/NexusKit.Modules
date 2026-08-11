using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NexusKit.Sync.Protocol;

namespace NexusKit.Modules.Sync;

/// <summary>
/// Turns a failed HTTP response into a <see cref="SyncProblem"/>.
/// <para>Written defensively on purpose. A failure response is exactly the moment when the
/// thing on the other end might not be a sync server at all — a reverse proxy returning
/// its own 502 page, a captive portal, a misconfigured host serving HTML. Throwing a JSON
/// parse error there would replace a useful message with a misleading one.</para>
/// </summary>
internal static class ProblemDetailsReader
{
    public static async Task<SyncProblem> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;

        if (LooksLikeProblemDetails(response.Content.Headers.ContentType))
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                return FromDocument(document.RootElement, status);
            }
            catch (JsonException)
            {
                // Content-Type claimed problem+json and the body was not. Fall through to the
                // generic problem rather than surfacing a parse error the caller cannot act on.
            }
        }

        return Fallback(response.StatusCode, status);
    }

    private static bool LooksLikeProblemDetails(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is "application/problem+json" or "application/json";

    private static SyncProblem FromDocument(JsonElement root, int status)
    {
        if (root.ValueKind != JsonValueKind.Object) return Fallback((HttpStatusCode)status, status);

        string? type = null;
        string? title = null;
        string? detail = null;
        var reportedStatus = status;
        Dictionary<string, string>? extensions = null;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    type = property.Value.GetString();
                    break;
                case "title":
                    title = property.Value.GetString();
                    break;
                case "detail":
                    detail = property.Value.GetString();
                    break;
                case "status":
                    if (property.Value.TryGetInt32(out var parsed)) reportedStatus = parsed;
                    break;
                case "instance":
                    break;   // defined by RFC 9457 but carries nothing this client acts on
                default:
                    // Everything else is a type-specific extension. Flattened to strings so a
                    // client can read "the server knows 1.0 and 1.1" without this layer having
                    // to model every problem type.
                    extensions ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    extensions[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText();
                    break;
            }
        }

        return new SyncProblem(
            type ?? "about:blank",
            title ?? ReasonFor((HttpStatusCode)reportedStatus),
            reportedStatus,
            detail,
            extensions);
    }

    private static SyncProblem Fallback(HttpStatusCode statusCode, int status) =>
        new("about:blank", ReasonFor(statusCode), status);

    private static string ReasonFor(HttpStatusCode statusCode) => statusCode.ToString();
}
