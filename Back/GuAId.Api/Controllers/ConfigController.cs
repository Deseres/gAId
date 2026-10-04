using Microsoft.AspNetCore.Mvc;

namespace GuAId.Api.Controllers;

[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public ConfigController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var mapsKey = _configuration["Google:MapsApiKey"];
        if (string.IsNullOrWhiteSpace(mapsKey))
            mapsKey = _configuration["Google:ApiKey"];

        return Ok(new { googleMapsApiKey = mapsKey ?? "" });
    }
}
