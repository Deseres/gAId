using System.ClientModel;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using GuAId.Api.Models;
using OpenAI.Chat;

namespace GuAId.Api.Services;

public sealed class RouteService
{
    private const int StopCount = 5;
    private const double SearchRadiusMeters = 2000;
    private const double MaxStopMeters = 3000;
    private const double SamePlaceMeters = 30;

    private const string SystemPrompt =
        """
        You are a travel guide that proposes the next stop on a map.
        The message has "Current start" and "User request".
        Current start is where the user is now. It is fixed. Do not ask for a start.
        Do not switch to a different start written in the user request.
        The user request is the wish for this step: vibe, food, a change of plan, or trying again.
        If the user request is empty, suggest interesting places near the start.
        Reply with one JSON object and no other text. Use this shape:
        {
          "text": "short reply to the user",
          "locations": [
            { "name": "Place name, City", "description": "why this place fits this step" }
          ]
        }
        locations has exactly 5 real places within walking distance of the start that match the user request.
        Do not include the start itself.
        name is an English place name plus the city, specific enough for a map search, such as "Wawel Castle, Krakow".
        description is one short sentence saying why you suggest this place at this step.
        Write text and description in the language of the User request text, not the language of the country.
        If the User request is empty, write in English.
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

    public RouteService(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = configuration["OpenAI:ApiKey"];
        _googleApiKey = configuration["Google:ApiKey"];
        _model = string.IsNullOrWhiteSpace(configuration["OpenAI:Model"])
            ? "gpt-4o-mini"
            : configuration["OpenAI:Model"]!;
    }

    public async Task<RouteResponse> CreateAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        if (request.Start?.Lat is not double startLat || request.Start.Lng is not double startLng)
        {
            throw new RouteCallException(
                StatusCodes.Status400BadRequest,
                "Start with lat and lng is required.");
        }

        if (startLat is < -90 or > 90 || startLng is < -180 or > 180)
        {
            throw new RouteCallException(
                StatusCodes.Status400BadRequest,
                "Start lat must be between -90 and 90, lng between -180 and 180.");
        }

        var start = request.Start;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "Set OpenAI:ApiKey in appsettings.Development.json.");
        }

        if (string.IsNullOrWhiteSpace(_googleApiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "Set Google:ApiKey in appsettings.Development.json.");
        }

        var client = new ChatClient(_model, _apiKey);
        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(BuildUserMessage(start, startLat, startLng, request.Prompt))
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
            throw new RouteCallException(StatusCodes.Status502BadGateway, ex.Message);
        }

        var text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new RouteCallException(
                StatusCodes.Status502BadGateway,
                "The model returned an empty response.");
        }

        ModelRoute model;
        try
        {
            model = JsonSerializer.Deserialize<ModelRoute>(text, JsonOptions)
                ?? throw new JsonException("Empty route.");
        }
        catch (JsonException)
        {
            throw new RouteCallException(
                StatusCodes.Status502BadGateway,
                "The model returned JSON that does not match the route shape.");
        }

        var candidates = (model.Locations ?? [])
            .Where(location => !string.IsNullOrWhiteSpace(location.Name))
            .Take(StopCount)
            .ToArray();

        var excludedPlaceIds = (request.Visited ?? [])
            .Append(start.GooglePlaceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var httpClient = _httpClientFactory.CreateClient();
        var lookups = candidates
            .Select(location => FindPlaceAsync(httpClient, location, excludedPlaceIds, startLat, startLng, cancellationToken))
            .ToArray();

        Location?[] resolved;
        try
        {
            resolved = await Task.WhenAll(lookups);
        }
        catch (HttpRequestException ex)
        {
            throw new RouteCallException(StatusCodes.Status502BadGateway, ex.Message);
        }

        return new RouteResponse
        {
            Text = model.Text ?? "",
            Locations = resolved
                .OfType<Location>()
                .DistinctBy(location => location.GooglePlaceId)
                .ToList()
        };
    }

    private static string BuildUserMessage(RouteStart start, double lat, double lng, string? prompt)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(start.Name))
            parts.Add(start.Name.Trim());
        parts.Add($"coordinates {lat.ToString(CultureInfo.InvariantCulture)}, {lng.ToString(CultureInfo.InvariantCulture)}");

        return $"Current start: {string.Join(", ", parts)}\nUser request: {prompt?.Trim() ?? ""}";
    }

    private async Task<Location?> FindPlaceAsync(
        HttpClient httpClient,
        ModelLocation location,
        HashSet<string> excludedPlaceIds,
        double startLat,
        double startLng,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
        request.Headers.Add("X-Goog-FieldMask", "places.id,places.location,places.displayName");
        request.Content = JsonContent.Create(new
        {
            textQuery = location.Name!.Trim(),
            locationBias = new
            {
                circle = new
                {
                    center = new { latitude = startLat, longitude = startLng },
                    radius = SearchRadiusMeters
                }
            }
        });

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
        var displayName = place?.DisplayName?.Text?.Trim();
        if (place?.Location is null || string.IsNullOrWhiteSpace(place.Id) || string.IsNullOrWhiteSpace(displayName))
            return null;

        var lat = place.Location.Latitude;
        var lng = place.Location.Longitude;
        var distance = DistanceMeters(startLat, startLng, lat, lng);
        if (distance > MaxStopMeters || distance <= SamePlaceMeters)
            return null;

        if (excludedPlaceIds.Contains(place.Id))
            return null;

        return new Location
        {
            Name = displayName,
            GooglePlaceId = place.Id,
            Lat = lat,
            Lng = lng,
            Description = location.Description ?? ""
        };
    }

    private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadius = 6371000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLng = DegreesToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
            Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadius * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    private sealed class ModelRoute
    {
        public string? Text { get; set; }

        public List<ModelLocation>? Locations { get; set; }
    }

    private sealed class ModelLocation
    {
        public string? Name { get; set; }

        public string? Description { get; set; }
    }

    private sealed class PlacesSearchResponse
    {
        public List<PlaceHit>? Places { get; set; }
    }

    private sealed class PlaceHit
    {
        public string? Id { get; set; }

        public PlaceCoordinates? Location { get; set; }

        public PlaceDisplayName? DisplayName { get; set; }
    }

    private sealed class PlaceDisplayName
    {
        public string? Text { get; set; }
    }

    private sealed class PlaceCoordinates
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
