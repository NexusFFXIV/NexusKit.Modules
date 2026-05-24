using System.Text.Json.Serialization;

namespace NexusKit.Modules.FfxivCollect.Models;

public sealed class Character
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("data_center")] public string? DataCenter { get; set; }
    [JsonPropertyName("portrait")] public string? Portrait { get; set; }
    [JsonPropertyName("last_parsed")] public DateTime? LastParsed { get; set; }

    [JsonPropertyName("mounts")] public CategorySummary? Mounts { get; set; }
    [JsonPropertyName("minions")] public CategorySummary? Minions { get; set; }
    [JsonPropertyName("achievements")] public CategorySummary? Achievements { get; set; }
}

public sealed class CategorySummary
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("ranking")] public int? Ranking { get; set; }
    [JsonPropertyName("public")] public bool? IsPublic { get; set; }
}
