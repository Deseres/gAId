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
        You are a travel guide that proposes the next stop on a map.
        The user describes where they are and what they want.
        Reply with one JSON object and no other text. Use this shape:
        {
          "text": "short reply to the user",
          "city": "city name or empty string",
          "locations": [
            { "name": "place name", "description": "why this place fits this step" }
          ]
        }
        Start check: a start is a place the user is at or the place the route starts from, such as "I am at the station" or "we start from the hotel".
        If the user did not give a start, do not propose any stops.
        Then text asks them, in their language, to name the starting point, city is "", and locations is [].
        If the start is clear, city is that city in English, or "" if you do not know it.
        locations then has exactly 5 real places near that start that match the request.
        These are options for the next stop only. Do not include the start itself.
        Prefer the closest interesting places to the start.
        name is the place name in English only, without the city.
        description is one short sentence, in the user's language, saying why you suggest this place at this step.
        text is a short reply in the user's language.
        Do not add other fields. Do not invent coordinates or place ids.
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

            route.Text ??= "";
            route.Locations ??= [];

            var candidates = route.Locations
                .Where(location => !string.IsNullOrWhiteSpace(location.Name))
                .Take(5)
                .ToArray();
            if (candidates.Length == 0)
            {
                route.Locations = [];
                return Ok(route);
            }

            if (string.IsNullOrWhiteSpace(_googleApiKey))
            {
                return Problem(
                    detail: "Set Google:ApiKey in appsettings.Development.json.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var httpClient = _httpClientFactory.CreateClient();
            var lookups = candidates
                .Select(location => FindCoordinatesAsync(httpClient, location, route.City, cancellationToken))
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
        Location location,
        string city,
        CancellationToken cancellationToken)
    {
        var textQuery = string.IsNullOrWhiteSpace(city)
            ? location.Name
            : $"{location.Name}, {city}";
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
            Name = location.Name,
            GooglePlaceId = place.Id,
            Lat = place.Location.Latitude,
            Lng = place.Location.Longitude,
            Description = location.Description
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
