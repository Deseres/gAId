namespace GuAId.Api.Models;

public sealed class RouteResponse
{
    public string City { get; set; } = "";

    public List<Location> Locations { get; set; } = [];
}
