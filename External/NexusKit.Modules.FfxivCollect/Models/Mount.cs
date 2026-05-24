using System.Text.Json.Serialization;

namespace NexusKit.Modules.FfxivCollect.Models;

public sealed class Mount
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("patch")] public string? Patch { get; set; }
    [JsonPropertyName("owned")] public string? Owned { get; set; }
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("seats")] public int? Seats { get; set; }
}
