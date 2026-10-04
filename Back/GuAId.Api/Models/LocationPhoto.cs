using System.Text.Json.Serialization;

namespace GuAId.Api.Models;

public sealed class LocationPhoto
{
    public string Url { get; set; } = "";

    public string Author { get; set; } = "";

    [JsonPropertyName("author_uri")]
    public string AuthorUri { get; set; } = "";
}
