using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using IdempotencyKey.Store;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
//      b. non-IResult              → Release; throw IdempotencyFilterException
//      c. Captured 5xx             → Release; return snapshot (not cached)
//      d. Captured non-5xx         → Complete
//         i.  Complete succeeded   → return snapshot
//         ii. Complete threw       → Release; return 500 (idempotency persistence failure)
public sealed class IdempotencyKeyFilter<TRequest>(
    IIdempotencyKeyStore store,
    IOptions<IdempotencyKeyOptions> options,
    ILogger<IdempotencyKeyFilter<TRequest>> logger) : IEndpointFilter
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const string IdempotentReplayed = "Idempotent-Replayed";

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

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault()
            ?? throw new IdempotencyFilterException(
                $"IdempotencyKeyFilter<{typeof(TRequest).Name}> requires the endpoint to accept " +
                $"a parameter of type {typeof(TRequest).Name}, but none was found.");

        var requestHash = ComputeRequestHash(request);
        var acquireResult = await store.TryAcquire(key.Value, requestHash, httpContext.RequestAborted);

        if (!acquireResult.IsAcquired)
        {
            if (acquireResult.Response is not { IsCompleted: true })
            {
                return Results.Problem(
                    detail: "A request with this 'Idempotency-Key' is already being processed. Retry after it completes.",
                    title: "Concurrent Request",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!string.Equals(acquireResult.Response.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Results.Problem(
                    detail: "The 'Idempotency-Key' was already used with a different request payload.",
                    title: "Idempotency Key Reuse",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            httpContext.Response.Headers[IdempotentReplayed] = "true";
            return acquireResult.Response.Snapshot; // Replay cached result.
        }

        var ownershipToken = acquireResult.OwnershipToken;
        object? result;

        try
        {
            result = await next(context);
        }
        catch
        {
            await store.Release(key.Value, ownershipToken, CancellationToken.None);
            throw;
        }

        if (result is not IResult handlerResult)
        {
            await store.Release(key.Value, ownershipToken, CancellationToken.None);
            throw new IdempotencyFilterException($"IdempotencyKeyFilter<{typeof(TRequest).Name}> requires the handler to return IResult, got: {result?.GetType().FullName ?? "null"}");
        }

        var snapshot = await CaptureResult(handlerResult, httpContext);

        if (snapshot.Body.Length > options.Value.MaxCachedResponseBodySize)
        {
            logger.LogWarning(
                "Response body for idempotency key '{Key}' exceeds MaxCachedResponseBodySize ({Size} > {Limit}). Skipping cache.",
                key, snapshot.Body.Length, options.Value.MaxCachedResponseBodySize);
            await store.Release(key.Value, ownershipToken, CancellationToken.None);
            return snapshot;
        }

        // Do not cache 5xx — they are transient; the client must be able to retry.
        if (snapshot.StatusCode is >= 500 and <= 599)
        {
            await store.Release(key.Value, ownershipToken, CancellationToken.None);
            return snapshot;
        }

        try
        {
            await store.Complete(key.Value, ownershipToken, snapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist idempotency key '{Key}'", key);
            await store.Release(key.Value, ownershipToken, CancellationToken.None);

            return Results.Problem(
                detail: "The response could not be persisted for idempotency. The operation may have completed. Retry with the same Idempotency-Key.",
                title: "Idempotency Persistence Failure",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return snapshot;
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
    /// Executes an <see cref="IResult"/> against the real <see cref="HttpContext"/> with a
    /// body-swapped <see cref="MemoryStream"/> to capture the status code, response headers,
    /// and body bytes for storage. The original response body stream is restored in the finally
    /// block, and headers/status are cleared to prevent duplication when the returned
    /// <see cref="HttpResponseSnapshot"/> writes them again during the actual response.
    /// </summary>
    private static async Task<HttpResponseSnapshot> CaptureResult(IResult result, HttpContext httpContext)
    {
        var originalBody = httpContext.Response.Body;
        await using var memoryStream = new MemoryStream();

        try
        {
            httpContext.Response.Body = memoryStream;
            await result.ExecuteAsync(httpContext);

            var statusCode = httpContext.Response.StatusCode;
            var headers = httpContext.Response.Headers.ToDictionary();
            var body = memoryStream.ToArray();

            return new HttpResponseSnapshot(statusCode, headers, body);
        }
        finally
        {
            httpContext.Response.Body = originalBody;

            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.Headers.Clear();
            }
        }
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
