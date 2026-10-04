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

    public double? Rating { get; set; }

    [JsonPropertyName("user_rating_count")]
    public int? UserRatingCount { get; set; }

    [JsonPropertyName("rating_summary")]
    public string RatingSummary { get; set; } = "";

    [JsonPropertyName("review_summary")]
    public string ReviewSummary { get; set; } = "";

    [JsonPropertyName("price_level")]
    public string PriceLevel { get; set; } = "";

    public string Price { get; set; } = "";

    [JsonPropertyName("open_now")]
    public bool? OpenNow { get; set; }

    [JsonPropertyName("opening_hours")]
    public List<string> OpeningHours { get; set; } = [];

    public List<LocationPhoto> Photos { get; set; } = [];
}
