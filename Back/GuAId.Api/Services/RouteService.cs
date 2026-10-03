using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using GuAId.Api.Models;
using OpenAI.Chat;

namespace GuAId.Api.Services;

public sealed class RouteService
{
    private const double SearchRadiusMeters = 2000;
    private const double SamePlaceMeters = 30;

    private const string SystemPrompt =
        """
        You are a travel guide that proposes the next stop on a map.
        Each request is one step. The message has "Current start" and "User request".
        Reply with one JSON object and no other text. Use this shape:
        {
          "text": "short reply to the user",
          "city": "city name or empty string",
          "locations": [
            { "name": "place name", "description": "why this place fits this step" }
          ]
        }
        If Current start is "not provided":
        A start in the user request is a place they are at or the route starts from, such as "I am at the station" or "we start from the hotel".
        If the user request has no start, do not propose stops.
        Then text asks them, in their language, to name the starting point, city is "", and locations is [].
        If the user request names a start, use that place as the anchor.
        If Current start is a place or coordinates, that place is the anchor.
        Do not ask for a start. Do not switch to a different start written in the user request.
        The user request is only the wish for this step: vibe, food, a change of plan, or trying again.
        If the user request is empty, suggest interesting places near the anchor.
        When there is an anchor, city is the English city name, or "" if you do not know it.
        locations then has exactly 5 real places near the anchor that match the user request.
        These are options for the next stop only. Do not include the anchor itself.
        Prefer the closest interesting places to the anchor.
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
        var start = request.Start;
        if (HasPartialCoordinates(start))
        {
            throw new RouteCallException(
                StatusCodes.Status400BadRequest,
                "Start lat and lng must be sent together.");
        }

        if (!HasStart(start) && string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new RouteCallException(
                StatusCodes.Status400BadRequest,
                "Prompt or start is required.");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "Set OpenAI:ApiKey in appsettings.Development.json.");
        }

        var client = new ChatClient(_model, _apiKey);
        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(BuildUserMessage(request))
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

        RouteResponse route;
        try
        {
            route = JsonSerializer.Deserialize<RouteResponse>(text, JsonOptions)
                ?? throw new JsonException("Empty route.");
        }
        catch (JsonException)
        {
            throw new RouteCallException(
                StatusCodes.Status502BadGateway,
                "The model returned JSON that does not match RouteResponse.");
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
            return route;
        }

        if (string.IsNullOrWhiteSpace(_googleApiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "Set Google:ApiKey in appsettings.Development.json.");
        }

        var httpClient = _httpClientFactory.CreateClient();
        var lookups = candidates
            .Select(location => FindCoordinatesAsync(httpClient, location, route.City, start, cancellationToken))
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

        route.Locations = resolved.OfType<Location>().ToList();
        return route;
    }

    private static string BuildUserMessage(RouteRequest request)
    {
        var prompt = request.Prompt?.Trim() ?? "";
        if (!HasStart(request.Start))
            return $"Current start: not provided\nUser request: {prompt}";

        var start = request.Start!;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(start.Name))
            parts.Add(start.Name.Trim());
        if (start.Lat is double lat && start.Lng is double lng)
            parts.Add($"coordinates {lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        return $"Current start: {string.Join(", ", parts)}\nUser request: {prompt}";
    }

    private static bool HasStart(RouteStart? start) =>
        start is not null && (
            !string.IsNullOrWhiteSpace(start.Name) ||
            start.Lat is not null && start.Lng is not null);

    private static bool HasPartialCoordinates(RouteStart? start) =>
        start is not null && start.Lat is not null != start.Lng is not null;

    private async Task<Location?> FindCoordinatesAsync(
        HttpClient httpClient,
        Location location,
        string city,
        RouteStart? start,
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
        request.Content = JsonContent.Create(BuildPlacesBody(textQuery, start));

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

        if (IsCurrentStart(place.Id, place.Location.Latitude, place.Location.Longitude, start))
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

    private static object BuildPlacesBody(string textQuery, RouteStart? start)
    {
        if (start?.Lat is not double lat || start.Lng is not double lng)
            return new { textQuery };

        return new
        {
            textQuery,
            locationBias = new
            {
                circle = new
                {
                    center = new { latitude = lat, longitude = lng },
                    radius = SearchRadiusMeters
                }
            }
        };
    }

    private static bool IsCurrentStart(string placeId, double lat, double lng, RouteStart? start)
    {
        if (start is null)
            return false;

        if (!string.IsNullOrWhiteSpace(start.GooglePlaceId) &&
            string.Equals(start.GooglePlaceId, placeId, StringComparison.Ordinal))
            return true;

        return start.Lat is double startLat &&
            start.Lng is double startLng &&
            DistanceMeters(startLat, startLng, lat, lng) <= SamePlaceMeters;
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
