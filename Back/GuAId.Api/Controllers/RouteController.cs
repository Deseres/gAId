using GuAId.Api.Models;
using GuAId.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GuAId.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RouteController : ControllerBase
{
    private readonly RouteService _routes;

    public RouteController(RouteService routes)
    {
        _routes = routes;
    }

    [HttpPost]
    public async Task<ActionResult<RouteResponse>> Create(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _routes.CreateAsync(request, cancellationToken));
        }
        catch (RouteCallException ex)
        {
            return Problem(detail: ex.Message, statusCode: ex.StatusCode);
        }
    }
}
