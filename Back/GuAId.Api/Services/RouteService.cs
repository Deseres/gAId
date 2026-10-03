using System.ClientModel;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuAId.Api.Models;
using OpenAI.Chat;

namespace GuAId.Api.Services;

public sealed class RouteService
{
    private const int StopCount = 5;
    private const int MaxQueries = 3;
    private const int ResultsPerQuery = 10;
    private const double MaxStopMeters = 3000;
    private const double SamePlaceMeters = 30;
    private const double MetersPerDegree = 111320;

    private static readonly string[] DefaultQueries = ["historical landmark", "museum", "park"];

    private const string QueryPrompt =
        """
        You turn a traveler's message into Google Maps search queries for places near them.
        Reply with one JSON object and no other text: { "queries": ["..."] }
        Give 1 to 3 short English queries that Google Maps understands, such as "italian restaurant", "specialty coffee", "viewpoint", "park".
        The message can be in any language. Understand the wish behind it and answer with English queries.
        Use one query when the request is specific. Use up to 3 different queries when it is vague.
        Examples:
        "I want Italian food" -> ["italian restaurant"]
        "something romantic for the evening" -> ["romantic restaurant", "wine bar", "viewpoint"]
        "tired, want to sit somewhere quiet" -> ["quiet cafe", "park"]
        "куда сходить с ребенком" -> ["playground", "children museum", "park"]
        Do not include a city or an address. The search is already limited to the area around the traveler.
        Only if the message has no wish at all, such as random letters, return ["historical landmark", "museum", "park"].
        """;

    private const string PickPrompt =
        """
        You are a travel guide that proposes the next stop on a map.
        The message has "Current start", "User request" and "Places nearby".
        Places nearby is a numbered list of real places near the start, found on Google Maps.
        Each line has the number, the name, the type and the distance from the start.
        Pick up to 5 places from that list that best match the user request. Use only numbers from the list.
        If the user request is empty, pick the most interesting places.
        Pick places a visitor can go to or see. Skip travel agencies, tour operators, offices and shops unless the user asks for them.
        Reply with one JSON object and no other text. Use this shape:
        {
          "text": "short reply to the user",
          "locations": [
            { "n": 1, "description": "why this place fits this step" }
          ]
        }
        If nothing in the list fits the request, or the list is empty, locations is [].
        Then text says that you did not find this nearby and suggests something else to ask for.
        description is one short sentence about this place. Do not start it with the place name.
        Base it on the name and the type. Do not invent menus, prices or opening hours.
        Write text and description in the language of the User request text, not the language of the country.
        If the User request is empty, write in English.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RouteService> _logger;
    private readonly string? _apiKey;
    private readonly string? _googleApiKey;
    private readonly string _model;

    public RouteService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<RouteService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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

        var start = request.Start;
        var prompt = request.Prompt?.Trim() ?? "";
        var client = new ChatClient(_model, _apiKey);

        var queries = prompt.Length == 0
            ? DefaultQueries
            : await BuildQueriesAsync(client, prompt, cancellationToken);

        _logger.LogInformation("Places queries: {Queries}", string.Join(" | ", queries));

        var excludedPlaceIds = (request.Visited ?? [])
            .Append(start.GooglePlaceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var places = await FindNearbyAsync(queries, startLat, startLng, excludedPlaceIds, cancellationToken);

        var picked = await CompleteJsonAsync<ModelRoute>(
            client,
            PickPrompt,
            BuildPickMessage(start, startLat, startLng, prompt, places),
            cancellationToken);

        return new RouteResponse
        {
            Text = picked.Text ?? "",
            Locations = (picked.Locations ?? [])
                .Where(choice => choice.N >= 1 && choice.N <= places.Count)
                .DistinctBy(choice => choice.N)
                .Take(StopCount)
                .Select(choice =>
                {
                    var place = places[choice.N - 1];
                    return new Location
                    {
                        Name = place.Name,
                        GooglePlaceId = place.Id,
                        Lat = place.Lat,
                        Lng = place.Lng,
                        Description = choice.Description ?? ""
                    };
                })
                .ToList()
        };
    }

    private static async Task<string[]> BuildQueriesAsync(
        ChatClient client,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await CompleteJsonAsync<ModelQueries>(client, QueryPrompt, prompt, cancellationToken);
        var queries = (result.Queries ?? [])
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();

        return queries.Length > 0 ? queries : DefaultQueries;
    }

    private static async Task<T> CompleteJsonAsync<T>(
        ChatClient client,
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken) where T : class
    {
        List<ChatMessage> messages =
        [
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userMessage)
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

        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new JsonException("Empty model response.");
        }
        catch (JsonException)
        {
            throw new RouteCallException(
                StatusCodes.Status502BadGateway,
                "The model returned JSON that does not match the expected shape.");
        }
    }

    private async Task<List<NearbyPlace>> FindNearbyAsync(
        string[] queries,
        double startLat,
        double startLng,
        HashSet<string> excludedPlaceIds,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        List<PlaceHit>[] results;
        try
        {
            results = await Task.WhenAll(queries.Select(query =>
                SearchTextAsync(httpClient, query, startLat, startLng, cancellationToken)));
        }
        catch (HttpRequestException ex)
        {
            throw new RouteCallException(StatusCodes.Status502BadGateway, ex.Message);
        }

        var seen = new HashSet<string>(excludedPlaceIds, StringComparer.Ordinal);
        var places = new List<NearbyPlace>();
        foreach (var place in results.SelectMany(hits => hits))
        {
            var name = place.DisplayName?.Text?.Trim();
            if (place.Location is null || string.IsNullOrWhiteSpace(place.Id) || string.IsNullOrWhiteSpace(name))
                continue;

            if (place.BusinessStatus is "CLOSED_PERMANENTLY" or "CLOSED_TEMPORARILY")
                continue;

            var distance = DistanceMeters(startLat, startLng, place.Location.Latitude, place.Location.Longitude);
            if (distance > MaxStopMeters || distance <= SamePlaceMeters)
                continue;

            if (!seen.Add(place.Id))
                continue;

            places.Add(new NearbyPlace(
                place.Id,
                name,
                place.PrimaryTypeDisplayName?.Text?.Trim() ?? "",
                place.Location.Latitude,
                place.Location.Longitude,
                (int)Math.Round(distance)));
        }

        return places;
    }

    private async Task<List<PlaceHit>> SearchTextAsync(
        HttpClient httpClient,
        string query,
        double lat,
        double lng,
        CancellationToken cancellationToken)
    {
        var latDelta = MaxStopMeters / MetersPerDegree;
        var lngDelta = Math.Min(180, MaxStopMeters / (MetersPerDegree * Math.Cos(DegreesToRadians(lat))));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "places.id,places.displayName,places.location,places.primaryTypeDisplayName,places.businessStatus");
        request.Content = JsonContent.Create(new
        {
            textQuery = query,
            pageSize = ResultsPerQuery,
            locationRestriction = new
            {
                rectangle = new
                {
                    low = new
                    {
                        latitude = Math.Max(-90, lat - latDelta),
                        longitude = Math.Max(-180, lng - lngDelta)
                    },
                    high = new
                    {
                        latitude = Math.Min(90, lat + latDelta),
                        longitude = Math.Min(180, lng + lngDelta)
                    }
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
        return payload?.Places ?? [];
    }

    private static string BuildPickMessage(
        RouteStart start,
        double lat,
        double lng,
        string prompt,
        List<NearbyPlace> places)
    {
        var message = new StringBuilder();
        message.Append("Current start: ");
        if (!string.IsNullOrWhiteSpace(start.Name))
            message.Append(start.Name.Trim()).Append(", ");
        message.Append(CultureInfo.InvariantCulture, $"coordinates {lat}, {lng}\n");
        message.Append("User request: ").Append(prompt).Append('\n');

        if (places.Count == 0)
        {
            message.Append("Places nearby: none");
            return message.ToString();
        }

        message.Append("Places nearby:\n");
        for (var i = 0; i < places.Count; i++)
        {
            var place = places[i];
            var type = string.IsNullOrWhiteSpace(place.Type) ? "place" : place.Type;
            message.Append(CultureInfo.InvariantCulture, $"{i + 1}. {place.Name} | {type} | {place.DistanceMeters} m\n");
        }

        return message.ToString();
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

    private sealed record NearbyPlace(string Id, string Name, string Type, double Lat, double Lng, int DistanceMeters);

    private sealed class ModelQueries
    {
        public List<string?>? Queries { get; set; }
    }

    private sealed class ModelRoute
    {
        public string? Text { get; set; }

        public List<ModelChoice>? Locations { get; set; }
    }

    private sealed class ModelChoice
    {
        public int N { get; set; }

        public string? Description { get; set; }
    }

    private sealed class PlacesSearchResponse
    {
        public List<PlaceHit>? Places { get; set; }
    }

    private sealed class PlaceHit
    {
        public string? Id { get; set; }

        public PlaceText? DisplayName { get; set; }

        public PlaceCoordinates? Location { get; set; }

        public PlaceText? PrimaryTypeDisplayName { get; set; }

        public string? BusinessStatus { get; set; }
    }

    private sealed class PlaceText
    {
        public string? Text { get; set; }
    }

    private sealed class PlaceCoordinates
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
