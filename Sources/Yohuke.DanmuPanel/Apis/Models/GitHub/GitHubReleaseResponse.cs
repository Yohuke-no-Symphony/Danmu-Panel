using System.Text.Json.Serialization;

namespace Yohuke.DanmuPanel.Apis.Models.GitHub;

internal sealed class GitHubReleaseResponse
{
    [JsonRequired]
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonRequired]
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonRequired]
    [JsonPropertyName("draft")]
    public bool IsDraft { get; init; }

    [JsonRequired]
    [JsonPropertyName("prerelease")]
    public bool IsPrerelease { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}
