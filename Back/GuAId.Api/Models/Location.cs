using System.Text.Json.Serialization;

namespace GuAId.Api.Models;

public sealed class Location
{
    public string Name { get; set; } = "";

    [JsonPropertyName("google_place_id")]
    public string GooglePlaceId { get; set; } = "";

    public double Lat { get; set; }

    public double Lng { get; set; }

    public string Description { get; set; } = "";

    [JsonPropertyName("photo_url")]
    public string PhotoUrl { get; set; } = "";

    [JsonPropertyName("photo_author")]
    public string PhotoAuthor { get; set; } = "";

    [JsonPropertyName("photo_author_uri")]
    public string PhotoAuthorUri { get; set; } = "";
}
