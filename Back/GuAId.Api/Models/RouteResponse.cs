namespace GuAId.Api.Models;

public sealed class RouteResponse
{
    public string Text { get; set; } = "";

    public string City { get; set; } = "";

    public List<Location> Locations { get; set; } = [];
}
