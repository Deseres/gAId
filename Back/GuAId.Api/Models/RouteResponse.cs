namespace GuAId.Api.Models;

public sealed class RouteResponse
{
    public string Text { get; set; } = "";

    public List<Location> Locations { get; set; } = [];
}
