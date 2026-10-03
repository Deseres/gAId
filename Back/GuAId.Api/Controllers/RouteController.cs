using System.ClientModel;
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
          "locations": [
            { "name": "place name" }
          ]
        }
        locations is an ordered list of real places.
        name is the place name in English only.
        Do not add other fields.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string? _apiKey;
    private readonly string _model;

    public RouteController(IConfiguration configuration)
    {
        _apiKey = configuration["OpenAI:ApiKey"];
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

            return Ok(route);
        }
        catch (JsonException)
        {
            return Problem(
                detail: "The model returned JSON that does not match RouteResponse.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
