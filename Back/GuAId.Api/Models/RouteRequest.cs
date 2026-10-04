namespace GuAId.Api.Models;

public sealed class RouteRequest
{
    public string? Prompt { get; set; }

    public string? Mode { get; set; }

    public PlanStep? Step { get; set; }

    public RouteStart? Start { get; set; }

    public List<string>? Visited { get; set; }
}
