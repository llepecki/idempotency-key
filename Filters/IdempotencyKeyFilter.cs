using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using IdempotencyKey.Store;
using Microsoft.AspNetCore.Mvc;

namespace IdempotencyKey.Filters;

// Decision tree:
//   1. Missing header              → 400
//   2. Multiple header values      → 400
//   3. Key is not a valid UUID     → 400
//   4. Entry exists, hash mismatch → 422
//   5. Entry exists, hash match    → replay cached IResult
//   6. Entry in-progress (409)     → 409 Conflict
//   7. Lock acquired               → execute handler
//      a. Handler throws           → Release; rethrow
//      b. 5xx result               → Release; return 5xx
//      c. non-5xx IResult          → Complete; return result
//      d. non-IResult              → Release; throw InvalidOperationException
public sealed class IdempotencyKeyFilter<TRequest>(IIdempotencyKeyStore store) : IEndpointFilter
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!TryGetIdempotencyKey(httpContext.Request.Headers, out var key, out var error))
        {
            return Results.Problem(
                detail: error.Detail,
                title: error.Title,
                statusCode: error.Status);
        }

        var request = context.GetArgument<TRequest>(0);
        var requestHash = ComputeRequestHash(request);

        var acquireResult = await store.TryAcquire(key.Value, requestHash, httpContext.RequestAborted);

        if (!acquireResult.IsAcquired)
        {
            if (acquireResult.Entry?.Result is null)
            {
                return Results.Problem(
                    detail: "A request with this 'Idempotency-Key' is already being processed. Retry after it completes.",
                    title: "Concurrent Request",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!string.Equals(acquireResult.Entry.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Results.Problem(
                    detail: "The 'Idempotency-Key' was already used with a different request payload.",
                    title: "Idempotency Key Reuse",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return acquireResult.Entry.Result; // Replay cached result.
        }

        object? result;

        try
        {
            result = await next(context);
        }
        catch
        {
            await store.Release(key.Value, httpContext.RequestAborted);
            throw;
        }

        // Do not cache 5xx — they are transient; the client must be able to retry.
        if (result is IStatusCodeHttpResult { StatusCode: >= 500 and <= 599 })
        {
            await store.Release(key.Value, httpContext.RequestAborted);
            return result;
        }

        if (result is not IResult iResult)
        {
            await store.Release(key.Value, httpContext.RequestAborted);

            throw new InvalidOperationException(
                $"IdempotencyKeyFilter<{typeof(TRequest).Name}> requires the handler to return IResult. " +
                $"Got: {result?.GetType().FullName ?? "null"}.");
        }

        var cachedResult = await CaptureResultAsync(iResult, httpContext.RequestServices);
        await store.Complete(key.Value, cachedResult, httpContext.RequestAborted);
        return iResult;
    }

    private static bool TryGetIdempotencyKey(
        IHeaderDictionary headers,
        [NotNullWhen(true)] out Guid? idempotencyKey,
        [NotNullWhen(false)] out ProblemDetails? error)
    {
        idempotencyKey = null;
        error = null;

        if (!headers.TryGetValue(IdempotencyKeyHeaderName, out var values))
        {
            error = new ProblemDetails
            {
                Title = "Missing Idempotency Key",
                Detail = "The 'Idempotency-Key' header is required",
                Status = StatusCodes.Status400BadRequest
            };

            return false;
        }

        if (values.Count != 1)
        {
            error = new ProblemDetails
            {
                Title = "Ambiguous Idempotency Key",
                Detail = "The 'Idempotency-Key' header must appear exactly once",
                Status = StatusCodes.Status400BadRequest
            };

            return false;
        }

        if (!Guid.TryParse(values[0], out Guid parsed))
        {
            error = new ProblemDetails
            {
                Title = "Invalid Idempotency Key",
                Detail = "The 'Idempotency-Key' header must be a valid UUID",
                Status = StatusCodes.Status400BadRequest
            };

            return false;
        }

        idempotencyKey = parsed;
        return true;
    }

    /// <summary>
    /// Executes an <see cref="IResult"/> against a <see cref="DefaultHttpContext"/> to capture
    /// the status code, response headers, and body bytes for storage.
    /// Uses the request-scoped <paramref name="requestServices"/> to avoid captive dependency issues.
    /// </summary>
    private static async Task<CachedResult> CaptureResultAsync(IResult result, IServiceProvider requestServices)
    {
        await using var memoryStream = new MemoryStream();
        var captureContext = new DefaultHttpContext
        {
            RequestServices = requestServices,
            Response = { Body = memoryStream }
        };

        await result.ExecuteAsync(captureContext);

        var statusCode = captureContext.Response.StatusCode;

        var headers = new Dictionary<string, string[]>();
        foreach (var (name, values) in captureContext.Response.Headers)
        {
            headers[name] = values.ToArray()!;
        }

        var body = memoryStream.ToArray();

        return new CachedResult(statusCode, headers, body);
    }

    // Fingerprint: SHA256 of JSON-serialized bound model arguments.
    // Request body is consumed by model binding before the filter runs;
    // serializing the bound argument is semantically equivalent for JSON endpoints.
    private static string ComputeRequestHash(TRequest request)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request);
        return Convert.ToHexString(SHA256.HashData(json));
    }
}
