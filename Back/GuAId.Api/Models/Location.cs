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
}
