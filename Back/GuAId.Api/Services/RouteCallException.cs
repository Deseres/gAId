namespace GuAId.Api.Services;

public sealed class RouteCallException : Exception
{
    public int StatusCode { get; }

    public RouteCallException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
