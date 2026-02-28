using Microsoft.Extensions.Primitives;

namespace IdempotencyKey.Store;

/// <summary>
/// Reconstructs a cached HTTP response from stored status code, headers, and body.
/// Implements <see cref="IStatusCodeHttpResult"/> so the filter's 5xx check works on replay.
/// </summary>
public sealed class CachedResult(
    int statusCode,
    Dictionary<string, string[]> headers,
    byte[] body) : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => statusCode;
    public Dictionary<string, string[]> Headers => headers;
    public byte[] Body => body;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = statusCode;

        foreach (var (name, values) in headers)
        {
            httpContext.Response.Headers[name] = new StringValues(values);
        }

        if (body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(body);
        }
    }
}
