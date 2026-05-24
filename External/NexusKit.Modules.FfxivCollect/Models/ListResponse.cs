using System.Text.Json.Serialization;

namespace NexusKit.Modules.FfxivCollect.Models;

public sealed class ListResponse<T>
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("results")] public IReadOnlyList<T> Results { get; set; } = Array.Empty<T>();
}
