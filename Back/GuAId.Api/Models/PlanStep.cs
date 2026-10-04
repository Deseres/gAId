namespace GuAId.Api.Models;

public sealed class PlanStep
{
    public string? Label { get; set; }

    public List<PlanQuery>? Queries { get; set; }
}
