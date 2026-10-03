using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using GuAId.Api.Models;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;

namespace GuAId.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RouteController : ControllerBase
{
    private const string SystemPrompt =
        """
        You are a travel guide that builds a route for a map.
        The user describes a place and what they want to do.
        Reply with one JSON object and no other text. Use this shape:
        {
          "city": "city name",
          "locations": [
            { "name": "place name" }
          ]
        }
        city is the main city of the route, in English.
        locations is an ordered list of real places in that city.
        name is the place name in English only, without the city.
        Do not add other fields.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _apiKey;
    private readonly string? _googleApiKey;
    private readonly string _model;

    public RouteController(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["OpenAI:ApiKey"];
        _googleApiKey = configuration["Google:ApiKey"];
        _model = string.IsNullOrWhiteSpace(configuration["OpenAI:Model"])
            ? "gpt-4o-mini"
            : configuration["OpenAI:Model"]!;
    }

    [HttpPost]
    public async Task<ActionResult<RouteResponse>> Create(
        [FromBody] RouteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Problem(
                detail: "Prompt is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return Problem(
                detail: "Set OpenAI:ApiKey in appsettings.Development.json.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var client = new ChatClient(_model, _apiKey);
        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(request.Prompt)
        ];
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };

        ClientResult<ChatCompletion> result;
        try
        {
            result = await client.CompleteChatAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        var text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Problem(
                detail: "The model returned an empty response.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            var route = JsonSerializer.Deserialize<RouteResponse>(text, JsonOptions);
            if (route is null)
            {
                return Problem(
                    detail: "The model returned an empty route.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            if (string.IsNullOrWhiteSpace(_googleApiKey))
            {
                return Problem(
                    detail: "Set Google:ApiKey in appsettings.Development.json.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var httpClient = _httpClientFactory.CreateClient();
            var lookups = route.Locations
                .Where(location => !string.IsNullOrWhiteSpace(location.Name))
                .Select(location => FindCoordinatesAsync(httpClient, location.Name, route.City, cancellationToken))
                .ToArray();

            Location?[] resolved;
            try
            {
                resolved = await Task.WhenAll(lookups);
            }
            catch (HttpRequestException ex)
            {
                return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }

            route.Locations = resolved.OfType<Location>().ToList();
            return Ok(route);
        }
        catch (JsonException)
        {
            return Problem(
                detail: "The model returned JSON that does not match RouteResponse.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<Location?> FindCoordinatesAsync(
        HttpClient httpClient,
        string name,
        string city,
        CancellationToken cancellationToken)
    {
        var textQuery = string.IsNullOrWhiteSpace(city) ? name : $"{name}, {city}";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
        request.Headers.Add("X-Goog-FieldMask", "places.id,places.location");
        request.Content = JsonContent.Create(new { textQuery });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Google Places returned {(int)response.StatusCode}: {error}");
        }

        var payload = await response.Content.ReadFromJsonAsync<PlacesSearchResponse>(
            JsonOptions,
            cancellationToken);
        var place = payload?.Places?.FirstOrDefault();
        if (place?.Location is null || string.IsNullOrWhiteSpace(place.Id))
            return null;

        return new Location
        {
            Name = name,
            PlaceId = place.Id,
            Lat = place.Location.Latitude,
            Lng = place.Location.Longitude
        };
    }

    private sealed class PlacesSearchResponse
    {
        public List<PlaceHit>? Places { get; set; }
    }

    private sealed class PlaceHit
    {
        public string? Id { get; set; }

        public PlaceCoordinates? Location { get; set; }
    }

    private sealed class PlaceCoordinates
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
