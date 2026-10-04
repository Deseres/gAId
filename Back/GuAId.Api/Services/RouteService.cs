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
    private const int StopCount = 10;
    private const int MaxQueries = 3;
    private const int MaxPlanSteps = 4;
    private const int ResultsPerQuery = 20;
    private const double MaxStopMeters = 3000;
    private const double MaxNamedPlaceMeters = 25000;
    private const double SamePlaceMeters = 30;
    private const double MetersPerDegree = 111320;
    private const int PhotoMaxWidthPx = 800;
    private const int MaxPhotos = 10;

    private static readonly PlaceQuery[] DefaultQueries =
    [
        new("historical landmark", false),
        new("museum", false),
        new("park", false)
    ];

    private const string QueryPrompt =
        """
        You turn a traveler's message into Google Maps search queries.
        Reply with one JSON object and no other text: { "queries": [ { "text": "...", "named": false } ] }
        Give 1 to 3 short English queries that Google Maps understands, such as "italian restaurant", "specialty coffee", "viewpoint", "park".
        The message can be in any language. Understand the wish behind it and answer with English queries.
        Correct obvious misspellings of a place name to the real English name Google Maps uses.
        Use one query when the request is specific. Use up to 3 different queries when it is vague.
        Set named to true only when text is the proper name of one particular place, such as "Wawel Castle".
        Set named to false when text is a type of place, a mood or an activity, such as "castle", "cafe" or "park".
        Examples:
        "I want Italian food" -> [{ "text": "italian restaurant", "named": false }]
        "something romantic for the evening" -> [{ "text": "romantic restaurant", "named": false }, { "text": "wine bar", "named": false }, { "text": "viewpoint", "named": false }]
        "tired, want to sit somewhere quiet" -> [{ "text": "quiet cafe", "named": false }, { "text": "park", "named": false }]
        "куда сходить с ребенком" -> [{ "text": "playground", "named": false }, { "text": "children museum", "named": false }, { "text": "park", "named": false }]
        "замок вавелл" -> [{ "text": "Wawel Castle", "named": true }]
        "хочу на Вавель и кофе" -> [{ "text": "Wawel Castle", "named": true }, { "text": "cafe", "named": false }]
        Do not include a city or an address.
        A query with named false is searched only near the traveler. A query with named true may be farther away.
        Only if the message has no wish at all, such as random letters, return [{ "text": "historical landmark", "named": false }, { "text": "museum", "named": false }, { "text": "park", "named": false }].
        """;

    private const string PlanPrompt =
        """
        You split a traveler's message into ordered stops.
        Reply with one JSON object and no other text:
        { "steps": [ { "label": "...", "queries": [ { "text": "...", "named": false } ] } ] }
        Each step is one stop. Each step has 1 to 3 short English queries that Google Maps understands.
        label is a short English name for the stop.
        The message can be in any language. labels and queries are always English. Never write a label in another language.
        Correct obvious misspellings of a place name to the real English name Google Maps uses.
        Set named to true only when text is the proper name of one particular place, such as "Wawel Castle".
        Set named to false when text is a type of place, a mood or an activity, such as "castle", "cafe" or "park".
        Do not include a city or an address.
        Use one query when the stop is specific. Use up to 3 queries when that one stop is vague.
        Return at most 4 steps.
        Split into separate steps only in these cases:
        - The message marks an order, with words such as "потом", "затем", "сначала", "после", "then", "after" or "next".
        - The message joins wishes with "и" or "and", and they cannot be the same business, such as a barber and a grocery store.
        Keep a single step when both wishes usually happen in one place, such as hookah and food, coffee and dessert, or food and wine.
        Keep a single step when it is unclear, such as a museum and coffee.
        A vague wish with no order is one step.
        Examples:
        "поесть, потом барбер, потом в магаз" -> [{ "label": "food", "queries": [{ "text": "restaurant", "named": false }] }, { "label": "barber shop", "queries": [{ "text": "barber shop", "named": false }] }, { "label": "shop", "queries": [{ "text": "supermarket", "named": false }] }]
        "кальян и покушать" -> [{ "label": "hookah and food", "queries": [{ "text": "hookah lounge", "named": false }] }]
        "кофе и десерт" -> [{ "label": "coffee", "queries": [{ "text": "cafe", "named": false }] }]
        "барбер и магазин" -> [{ "label": "barber shop", "queries": [{ "text": "barber shop", "named": false }] }, { "label": "shop", "queries": [{ "text": "supermarket", "named": false }] }]
        "музей и кофе" -> [{ "label": "museum and coffee", "queries": [{ "text": "museum", "named": false }, { "text": "cafe", "named": false }] }]
        "something romantic for the evening" -> [{ "label": "romantic evening", "queries": [{ "text": "romantic restaurant", "named": false }, { "text": "wine bar", "named": false }, { "text": "viewpoint", "named": false }] }]
        "замок вавелл" -> [{ "label": "Wawel Castle", "queries": [{ "text": "Wawel Castle", "named": true }] }]
        Only if the message has no wish at all, such as random letters, return one step with queries "historical landmark", "museum" and "park", all named false.
        """;

    private const string PickPrompt =
        """
        You are a travel guide that proposes the next stop on a map.
        The message has "Current start", "User request" and "Places nearby".
        It may also have "Current step" and "Upcoming steps".
        Places nearby is a numbered list of real places near the start, found on Google Maps.
        Each line has the number, the name, the type and the distance from the start.
        Pick up to 10 places from that list that match the user request. Use only numbers from the list.
        When at least 10 places fit, return 10. Return fewer only when fewer places in the list fit.
        A reasonable match is enough. Do not narrow the list to the two or three best places.
        If the user request is empty, pick the most interesting places, up to 10.
        If "Current step" is present, every picked place must fit that step.
        If "Upcoming steps" is present, mention those steps in text, in that order, and say they come after the user picks one of these places. Do not pick places for the later steps.
        A place several kilometers away is fine when the user named that specific place. Include it and mention how far it is.
        A type of place, such as a cafe or a park, should stay close to the start.
        Pick places a visitor can go to or see. Skip travel agencies, tour operators and offices unless the user asks for them.
        Reply with one JSON object and no other text. Use this shape:
        {
          "text": "short reply to the user",
          "locations": [
            { "n": 1, "description": "why this place fits this step", "rating_summary": "what the rating and reviews say" }
          ]
        }
        If nothing in the list fits the request, or the list is empty, locations is [].
        Then text says that you did not find this nearby and suggests something else to ask for.
        description is one short sentence about why this place fits. Do not start it with the place name.
        rating_summary states the score out of 5 and the review count, such as "4.5 out of 5, 5344 reviews".
        Add what visitors say only when that place's reviews line is not none, and only from that text.
        If the rating is missing, rating_summary is an empty string.
        If reviews is none, do not describe atmosphere, quality, service, reputation or a menu.
        description then only says why this type of place fits the request.
        Do not invent a rating, a review count, a price or opening hours.
        Mention a price or opening hours in description only when that place's line includes them.
        Write text, description and rating_summary in English only.
        Do this even when the User request, Current step or Upcoming steps are in another language.
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

        var mode = ParseMode(request.Mode);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "OpenAI API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_googleApiKey))
        {
            throw new RouteCallException(
                StatusCodes.Status500InternalServerError,
                "Google API key is not configured.");
        }

        var start = request.Start;
        var prompt = request.Prompt?.Trim() ?? "";
        var client = new ChatClient(_model, _apiKey);
        var search = await ResolveSearchAsync(client, mode, prompt, request.Step, cancellationToken);

        _logger.LogInformation(
            "Route mode {Mode}. Queries: {Queries}. Plan steps left: {PlanCount}",
            mode,
            string.Join(" | ", search.Queries.Select(query => query.Named ? $"{query.Text} (named)" : query.Text)),
            search.Plan.Count);

        var excludedPlaceIds = (request.Visited ?? [])
            .Append(start.GooglePlaceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var places = await FindNearbyAsync(search.Queries, startLat, startLng, excludedPlaceIds, cancellationToken);

        var picked = await CompleteJsonAsync<ModelRoute>(
            client,
            PickPrompt,
            BuildPickMessage(
                start,
                startLat,
                startLng,
                search.RequestText,
                search.StepLabel,
                search.Upcoming,
                places),
            cancellationToken);

        var choices = SelectChoices(picked.Locations, places);

        var httpClient = _httpClientFactory.CreateClient();
        var photos = await Task.WhenAll(choices.Select(choice =>
            ResolvePhotosAsync(httpClient, places[choice.N - 1].Photos, cancellationToken)));

        return new RouteResponse
        {
            Text = picked.Text ?? "",
            Plan = search.Plan,
            Locations = choices.Select((choice, index) =>
            {
                var place = places[choice.N - 1];
                return new Location
                {
                    Name = place.Name,
                    GooglePlaceId = place.Id,
                    Lat = place.Lat,
                    Lng = place.Lng,
                    Description = choice.Description ?? "",
                    Rating = place.Rating,
                    UserRatingCount = place.UserRatingCount,
                    RatingSummary = choice.RatingSummary ?? "",
                    ReviewSummary = place.ReviewSummary,
                    PriceLevel = place.PriceLevel,
                    Price = place.Price,
                    OpenNow = place.OpenNow,
                    OpeningHours = place.OpeningHours.ToList(),
                    Photos = photos[index].Select(photo => new LocationPhoto
                    {
                        Url = photo.Url,
                        Author = photo.Author,
                        AuthorUri = photo.AuthorUri
                    }).ToList()
                };
            }).ToList()
        };
    }

    private static string ParseMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "spot";

        var value = mode.Trim();
        if (value.Equals("spot", StringComparison.OrdinalIgnoreCase))
            return "spot";
        if (value.Equals("plan", StringComparison.OrdinalIgnoreCase))
            return "plan";

        throw new RouteCallException(
            StatusCodes.Status400BadRequest,
            "Mode must be spot or plan.");
    }

    private async Task<SearchRequest> ResolveSearchAsync(
        ChatClient client,
        string mode,
        string prompt,
        PlanStep? step,
        CancellationToken cancellationToken)
    {
        if (mode == "plan" && step is not null)
        {
            var queries = ReadStepQueries(step);
            var label = step.Label?.Trim() ?? "";
            var requestText = label.Length > 0 ? label : prompt;
            return new SearchRequest(queries, [], requestText, label, []);
        }

        if (prompt.Length == 0)
            return new SearchRequest(DefaultQueries, [], "", null, []);

        if (mode == "spot")
        {
            var queries = await BuildQueriesAsync(client, prompt, cancellationToken);
            return new SearchRequest(queries, [], prompt, null, []);
        }

        var steps = await BuildPlanAsync(client, prompt, cancellationToken);
        if (steps.Count == 0)
        {
            var queries = await BuildQueriesAsync(client, prompt, cancellationToken);
            return new SearchRequest(queries, [], prompt, null, []);
        }

        var plan = steps.Skip(1).Select(ToPlanStep).ToList();
        var upcoming = plan
            .Select(item => item.Label?.Trim() ?? "")
            .Where(label => label.Length > 0)
            .ToList();
        return new SearchRequest(steps[0].Queries, plan, prompt, steps[0].Label, upcoming);
    }

    private static async Task<List<ResolvedStep>> BuildPlanAsync(
        ChatClient client,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await CompleteJsonAsync<ModelPlan>(client, PlanPrompt, prompt, cancellationToken);
        return ParseSteps(result.Steps);
    }

    private static List<ResolvedStep> ParseSteps(List<JsonElement>? raw)
    {
        var steps = new List<ResolvedStep>();
        foreach (var item in raw ?? [])
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(item, "queries", out var queriesProp) ||
                queriesProp.ValueKind != JsonValueKind.Array)
                continue;

            var queries = ParseQueries(queriesProp.EnumerateArray().ToList());
            if (queries.Length == 0)
                continue;

            var label = "";
            if (TryGetProperty(item, "label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                label = labelProp.GetString()?.Trim() ?? "";
            if (label.Length == 0)
                label = queries[0].Text;
            if (label.Length > 80)
                label = label[..80].Trim();

            steps.Add(new ResolvedStep(label, queries));
            if (steps.Count == MaxPlanSteps)
                break;
        }

        return steps;
    }

    private static PlaceQuery[] ReadStepQueries(PlanStep step)
    {
        var queries = (step.Queries ?? [])
            .Select(query => new PlaceQuery((query.Text ?? "").Trim(), query.Named))
            .Where(query => query.Text.Length > 0)
            .DistinctBy(query => query.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();

        if (queries.Length == 0)
        {
            throw new RouteCallException(
                StatusCodes.Status400BadRequest,
                "Step needs at least one query with text.");
        }

        return queries;
    }

    private static PlanStep ToPlanStep(ResolvedStep step) => new()
    {
        Label = step.Label,
        Queries = step.Queries.Select(query => new PlanQuery
        {
            Text = query.Text,
            Named = query.Named
        }).ToList()
    };

    private static List<ModelChoice> SelectChoices(List<ModelChoice>? picked, List<NearbyPlace> places)
    {
        var choices = (picked ?? [])
            .Where(choice => choice.N >= 1 && choice.N <= places.Count)
            .DistinctBy(choice => choice.N)
            .Take(StopCount)
            .ToList();

        if (choices.Count == 0 || choices.Count >= StopCount)
            return choices;

        var used = choices.Select(choice => choice.N).ToHashSet();
        var extras = Enumerable.Range(1, places.Count)
            .Where(n => !used.Contains(n) && places[n - 1].DistanceMeters <= MaxStopMeters)
            .OrderBy(n => places[n - 1].DistanceMeters)
            .Take(StopCount - choices.Count);

        foreach (var n in extras)
            choices.Add(new ModelChoice { N = n, Description = "" });

        return choices;
    }

    private static async Task<PlaceQuery[]> BuildQueriesAsync(
        ChatClient client,
        string prompt,
        CancellationToken cancellationToken)
    {
        var result = await CompleteJsonAsync<ModelQueries>(client, QueryPrompt, prompt, cancellationToken);
        var queries = ParseQueries(result.Queries);
        return queries.Length > 0 ? queries : DefaultQueries;
    }

    private static PlaceQuery[] ParseQueries(List<JsonElement>? raw)
    {
        var queries = new List<PlaceQuery>();
        foreach (var item in raw ?? [])
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    queries.Add(new PlaceQuery(text, false));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object || !TryGetProperty(item, "text", out var textProp))
                continue;

            var name = textProp.ValueKind == JsonValueKind.String ? textProp.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var named = TryGetProperty(item, "named", out var namedProp) && IsTrue(namedProp);
            queries.Add(new PlaceQuery(name, named));
        }

        return queries
            .DistinctBy(query => query.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueries)
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True ||
        (value.ValueKind == JsonValueKind.String &&
         bool.TryParse(value.GetString(), out var parsed) &&
         parsed);

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
        PlaceQuery[] queries,
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
        for (var i = 0; i < queries.Length; i++)
        {
            var maxMeters = queries[i].Named ? MaxNamedPlaceMeters : MaxStopMeters;
            foreach (var place in results[i])
            {
                var name = place.DisplayName?.Text?.Trim();
                if (place.Location is null || string.IsNullOrWhiteSpace(place.Id) || string.IsNullOrWhiteSpace(name))
                    continue;

                if (place.BusinessStatus is "CLOSED_PERMANENTLY" or "CLOSED_TEMPORARILY")
                    continue;

                var distance = DistanceMeters(startLat, startLng, place.Location.Latitude, place.Location.Longitude);
                if (distance > maxMeters || distance <= SamePlaceMeters)
                    continue;

                if (!seen.Add(place.Id))
                    continue;

                var hours = place.RegularOpeningHours?.WeekdayDescriptions?
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList() ?? [];
                places.Add(new NearbyPlace(
                    place.Id,
                    name,
                    place.PrimaryTypeDisplayName?.Text?.Trim() ?? "",
                    place.Location.Latitude,
                    place.Location.Longitude,
                    (int)Math.Round(distance),
                    PhotosOf(place),
                    place.Rating,
                    place.UserRatingCount,
                    FormatPriceLevel(place.PriceLevel),
                    FormatPriceRange(place.PriceRange),
                    place.RegularOpeningHours?.OpenNow,
                    hours,
                    place.ReviewSummary?.Text?.Text?.Trim() ?? ""));
            }
        }

        return places;
    }

    private async Task<List<PlaceHit>> SearchTextAsync(
        HttpClient httpClient,
        PlaceQuery query,
        double lat,
        double lng,
        CancellationToken cancellationToken)
    {
        var radius = query.Named ? MaxNamedPlaceMeters : MaxStopMeters;
        var latDelta = radius / MetersPerDegree;
        var lngDelta = Math.Min(180, radius / (MetersPerDegree * Math.Cos(DegreesToRadians(lat))));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://places.googleapis.com/v1/places:searchText");
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "places.id,places.displayName,places.location,places.primaryTypeDisplayName,places.businessStatus,places.photos,places.rating,places.userRatingCount,places.priceLevel,places.priceRange,places.regularOpeningHours.openNow,places.regularOpeningHours.weekdayDescriptions,places.reviewSummary.text");
        request.Content = JsonContent.Create(new
        {
            textQuery = query.Text,
            languageCode = "en",
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
        string? stepLabel,
        IReadOnlyList<string> upcoming,
        List<NearbyPlace> places)
    {
        var message = new StringBuilder();
        message.Append("Current start: ");
        if (!string.IsNullOrWhiteSpace(start.Name))
            message.Append(start.Name.Trim()).Append(", ");
        message.Append(CultureInfo.InvariantCulture, $"coordinates {lat}, {lng}\n");
        message.Append("User request: ").Append(prompt).Append('\n');
        if (!string.IsNullOrWhiteSpace(stepLabel))
            message.Append("Current step: ").Append(stepLabel.Trim()).Append('\n');
        if (upcoming.Count > 0)
            message.Append("Upcoming steps: ").Append(string.Join(", ", upcoming)).Append('\n');

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
            message.Append(CultureInfo.InvariantCulture, $"{i + 1}. {place.Name} | {type} | {place.DistanceMeters} m");
            message.Append(" | rating: ").Append(FormatRating(place.Rating, place.UserRatingCount));
            message.Append(" | price: ").Append(FormatPriceLine(place.PriceLevel, place.Price));
            message.Append(" | open now: ").Append(place.OpenNow switch
            {
                true => "yes",
                false => "no",
                _ => "unknown"
            });
            message.Append(" | hours: ").Append(place.OpeningHours.Count == 0
                ? "none"
                : string.Join("; ", place.OpeningHours));
            message.Append(" | reviews: ").Append(place.ReviewSummary.Length == 0
                ? "none"
                : Clip(place.ReviewSummary, 400));
            message.Append('\n');
        }

        return message.ToString();
    }

    private static List<PlacePhotoRef> PhotosOf(PlaceHit place)
    {
        var photos = new List<PlacePhotoRef>();
        foreach (var photo in place.Photos ?? [])
        {
            if (string.IsNullOrWhiteSpace(photo.Name))
                continue;

            var author = photo.AuthorAttributions?
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.DisplayName));
            var authorUri = author?.Uri?.Trim() ?? "";
            if (authorUri.StartsWith("//", StringComparison.Ordinal))
                authorUri = "https:" + authorUri;

            photos.Add(new PlacePhotoRef(photo.Name, author?.DisplayName?.Trim() ?? "", authorUri));
            if (photos.Count == MaxPhotos)
                break;
        }

        return photos;
    }

    private async Task<List<PhotoLink>> ResolvePhotosAsync(
        HttpClient httpClient,
        IReadOnlyList<PlacePhotoRef> photos,
        CancellationToken cancellationToken)
    {
        if (photos.Count == 0)
            return [];

        var links = await Task.WhenAll(photos.Select(photo =>
            ResolvePhotoAsync(httpClient, photo, cancellationToken)));
        return links.Where(link => link is not null).Select(link => link!).ToList();
    }

    private async Task<PhotoLink?> ResolvePhotoAsync(
        HttpClient httpClient,
        PlacePhotoRef? photo,
        CancellationToken cancellationToken)
    {
        if (photo is null)
            return null;

        var segments = photo.Name.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var url = "https://places.googleapis.com/v1/"
            + string.Join('/', segments)
            + $"/media?maxWidthPx={PhotoMaxWidthPx}&skipHttpRedirect=true";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Goog-Api-Key", _googleApiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Place photo request failed with status {StatusCode}",
                    (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<PhotoMediaResponse>(
                JsonOptions,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.PhotoUri))
                return null;

            return new PhotoLink(payload.PhotoUri, photo.Author, photo.AuthorUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Place photo request failed");
            return null;
        }
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

    private sealed record PlaceQuery(string Text, bool Named);

    private sealed record ResolvedStep(string Label, PlaceQuery[] Queries);

    private sealed record SearchRequest(
        PlaceQuery[] Queries,
        List<PlanStep> Plan,
        string RequestText,
        string? StepLabel,
        IReadOnlyList<string> Upcoming);

    private sealed record PlacePhotoRef(string Name, string Author, string AuthorUri);

    private sealed record PhotoLink(string Url, string Author, string AuthorUri);

    private static string FormatRating(double? rating, int? count)
    {
        if (rating is not double value)
            return "none";

        var text = value.ToString("0.0", CultureInfo.InvariantCulture);
        return count is int reviews
            ? $"{text} from {reviews} reviews"
            : text;
    }

    private static string FormatPriceLine(string priceLevel, string price)
    {
        if (priceLevel.Length == 0 && price.Length == 0)
            return "none";
        if (priceLevel.Length == 0)
            return price;
        if (price.Length == 0)
            return priceLevel;
        return $"{priceLevel}, {price}";
    }

    private static string FormatPriceLevel(string? level) => level switch
    {
        "PRICE_LEVEL_FREE" => "free",
        "PRICE_LEVEL_INEXPENSIVE" => "inexpensive",
        "PRICE_LEVEL_MODERATE" => "moderate",
        "PRICE_LEVEL_EXPENSIVE" => "expensive",
        "PRICE_LEVEL_VERY_EXPENSIVE" => "very expensive",
        _ => ""
    };

    private static string FormatPriceRange(PlacePriceRange? range)
    {
        var start = FormatAmount(range?.StartPrice);
        var end = FormatAmount(range?.EndPrice);
        if (start.Length == 0 && end.Length == 0)
            return "";

        var amount = start.Length > 0 && end.Length > 0 && start != end
            ? $"{start}–{end}"
            : start.Length > 0 ? start : end;
        var currency = range?.StartPrice?.CurrencyCode;
        if (string.IsNullOrWhiteSpace(currency))
            currency = range?.EndPrice?.CurrencyCode;
        return string.IsNullOrWhiteSpace(currency) ? amount : $"{amount} {currency}";
    }

    private static string FormatAmount(PlaceMoney? money)
    {
        if (money is null)
            return "";
        if (string.IsNullOrWhiteSpace(money.Units) && money.Nanos == 0)
            return "";
        if (!long.TryParse(money.Units, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            units = 0;
        if (money.Nanos == 0)
            return units.ToString(CultureInfo.InvariantCulture);

        var value = units + money.Nanos / 1_000_000_000d;
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Clip(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max].Trim() + "...";
    }

    private sealed record NearbyPlace(
        string Id,
        string Name,
        string Type,
        double Lat,
        double Lng,
        int DistanceMeters,
        IReadOnlyList<PlacePhotoRef> Photos,
        double? Rating,
        int? UserRatingCount,
        string PriceLevel,
        string Price,
        bool? OpenNow,
        IReadOnlyList<string> OpeningHours,
        string ReviewSummary);

    private sealed class ModelQueries
    {
        public List<JsonElement>? Queries { get; set; }
    }

    private sealed class ModelPlan
    {
        public List<JsonElement>? Steps { get; set; }
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

        [JsonPropertyName("rating_summary")]
        public string? RatingSummary { get; set; }
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

        public double? Rating { get; set; }

        public int? UserRatingCount { get; set; }

        public string? PriceLevel { get; set; }

        public PlacePriceRange? PriceRange { get; set; }

        public PlaceOpeningHours? RegularOpeningHours { get; set; }

        public PlaceReviewSummary? ReviewSummary { get; set; }

        public List<PlacePhoto>? Photos { get; set; }
    }

    private sealed class PlacePriceRange
    {
        public PlaceMoney? StartPrice { get; set; }

        public PlaceMoney? EndPrice { get; set; }
    }

    private sealed class PlaceMoney
    {
        public string? CurrencyCode { get; set; }

        public string? Units { get; set; }

        public int Nanos { get; set; }
    }

    private sealed class PlaceOpeningHours
    {
        public bool? OpenNow { get; set; }

        public List<string>? WeekdayDescriptions { get; set; }
    }

    private sealed class PlaceReviewSummary
    {
        public PlaceText? Text { get; set; }
    }

    private sealed class PlacePhoto
    {
        public string? Name { get; set; }

        public List<PhotoAuthor>? AuthorAttributions { get; set; }
    }

    private sealed class PhotoAuthor
    {
        public string? DisplayName { get; set; }

        public string? Uri { get; set; }
    }

    private sealed class PhotoMediaResponse
    {
        public string? PhotoUri { get; set; }
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
