using System.Text.Json;
using Dapper;
using IdempotencyKey.Filters;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace IdempotencyKey.Store;

/// <summary>
/// PostgreSQL-backed idempotency key store using Dapper + Npgsql.
/// Registered as singleton — safe because connections are created per-operation
/// via Npgsql's internal connection pool.
///
/// <para><b>Optimization strategy:</b></para>
/// <para>
/// TryAcquire is the hot path (every request). Latency is reduced by running
/// eviction probabilistically (~10% of requests) and using INSERT ... ON CONFLICT
/// DO NOTHING to detect key conflicts without a separate existence check. On
/// conflict, a second query reads the existing entry.
/// </para>
/// <para>
/// <c>Complete</c> receives a <see cref="HttpResponseSnapshot"/> (already captured by the filter)
/// and serializes its properties for storage. The store has no HTTP concerns.
/// Complete and Release validate the ownership token to prevent stale callers from
/// corrupting a re-acquired lock.
/// </para>
/// </summary>
public sealed class PostgresIdempotencyKeyStore(
    NpgsqlDataSource dataSource,
    IOptions<IdempotencyKeyOptions> options,
    ILogger<PostgresIdempotencyKeyStore> logger) : IIdempotencyKeyStore
{
    private static readonly IReadOnlyDictionary<string, StringValues> EmptyHeaders =
        new Dictionary<string, StringValues>().AsReadOnly();

    private readonly IdempotencyKeyOptions _options = options.Value;

    public async ValueTask<AcquireResult> TryAcquire(Guid key, string requestHash, CancellationToken cancellationToken)
    {
        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // Bounded eviction: LIMIT 100 caps the work per eviction run so a large backlog of
        // expired entries doesn't spike latency on a single request. FOR UPDATE SKIP LOCKED
        // prevents concurrent eviction runs from contending on the same rows — if two requests
        // both trigger eviction simultaneously, they clean up different rows without blocking.
        // Sentinels use LockTimeout (default 5 min) as their expires_at, so stale sentinels
        // from crashed/hung requests are evicted naturally alongside completed entries.
        if (Random.Shared.Next(_options.EvictionRate) == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM idempotency_keys
                WHERE key IN (
                    SELECT key FROM idempotency_keys
                    WHERE expires_at < now()
                    LIMIT 100
                    FOR UPDATE SKIP LOCKED
                )
                """,
                cancellationToken: cancellationToken));
        }

        var ownershipToken = Guid.NewGuid();

        var insertedKey = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO idempotency_keys (key, request_hash, ownership_token, status_code, headers, body, expires_at)
            VALUES (@Key, @RequestHash, @OwnershipToken, NULL, NULL, NULL, now() + @LockTimeout)
            ON CONFLICT (key) DO NOTHING
            RETURNING key
            """,
            new { Key = key, RequestHash = requestHash, OwnershipToken = ownershipToken, LockTimeout = _options.LockTimeout },
            cancellationToken: cancellationToken));

        if (insertedKey.HasValue)
        {
            return AcquireResult.Acquired(ownershipToken);
        }

        // Conflict — delete if expired, then retry INSERT to re-acquire.
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_keys WHERE key = @Key AND expires_at < now()",
            new { Key = key },
            cancellationToken: cancellationToken));

        // Retry INSERT once: if the expired row was removed, this succeeds.
        var retryKey = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO idempotency_keys (key, request_hash, ownership_token, status_code, headers, body, expires_at)
            VALUES (@Key, @RequestHash, @OwnershipToken, NULL, NULL, NULL, now() + @LockTimeout)
            ON CONFLICT (key) DO NOTHING
            RETURNING key
            """,
            new { Key = key, RequestHash = requestHash, OwnershipToken = ownershipToken, LockTimeout = _options.LockTimeout },
            cancellationToken: cancellationToken));

        if (retryKey.HasValue)
        {
            return AcquireResult.Acquired(ownershipToken);
        }

        // Still conflicting — another request acquired between DELETE and retry INSERT.
        // Read the existing (non-expired) row.
        var row = await connection.QuerySingleOrDefaultAsync<ExistingRow>(new CommandDefinition(
            """
            SELECT request_hash RequestHash, status_code StatusCode, headers Headers, body Body, expires_at ExpiresAt
            FROM idempotency_keys
            WHERE key = @Key
            """,
            new { Key = key },
            cancellationToken: cancellationToken));

        if (row?.StatusCode is null)
        {
            return AcquireResult.NotAcquired();
        }

        var headers = DeserializeHeaders(row.Headers);
        var cachedResult = new HttpResponseSnapshot(row.StatusCode.Value, headers, row.Body ?? Array.Empty<byte>());
        var response = new StoredResponse(row.RequestHash, cachedResult, row.ExpiresAt);

        return AcquireResult.NotAcquired(response);
    }

    public async ValueTask Complete(Guid key, Guid ownershipToken, HttpResponseSnapshot snapshot, CancellationToken cancellationToken)
    {
        var headersJson = SerializeHeaders(snapshot.Headers);

        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // This UPDATE only modifies non-PK-indexed columns (status_code, headers, body,
        // expires_at). Because the schema has no secondary indexes, PostgreSQL can perform
        // a HOT (Heap-Only Tuple) update — the new tuple version is placed on the same heap
        // page (aided by fillfactor=50) without creating any new index entries. This avoids
        // the expensive MVCC index maintenance that the previous partial index on expires_at
        // was forcing on every Complete call.
        //
        // The ::json cast matches the column type (JSON, not JSONB). JSON skips binary
        // decomposition on write since headers are only stored/retrieved whole.
        //
        // Ownership token check prevents a stale caller from overwriting a re-acquired lock.
        var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE idempotency_keys
            SET
                status_code = @StatusCode,
                headers     = @Headers::json,
                body        = @Body,
                expires_at  = now() + @Ttl
            WHERE key = @Key AND ownership_token = @OwnershipToken
            """,
            new
            {
                Key = key,
                OwnershipToken = ownershipToken,
                StatusCode = snapshot.StatusCode,
                Headers = headersJson,
                Body = snapshot.Body,
                Ttl = _options.Ttl
            },
            cancellationToken: cancellationToken));

        if (rowsAffected == 0)
        {
            throw new IdempotencyStoreException($"Can't complete idempotency key '{key}': ownership token mismatch or entry not found (stale caller or concurrent eviction)");
        }
    }

    public async ValueTask Release(Guid key, Guid ownershipToken, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = dataSource.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            // Ownership token check prevents a stale caller from deleting a re-acquired lock.
            var rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM idempotency_keys WHERE key = @Key AND ownership_token = @OwnershipToken",
                new { Key = key, OwnershipToken = ownershipToken },
                cancellationToken: cancellationToken));

            if (rowsAffected == 0)
            {
                logger.LogWarning("Can't release idempotency key '{Key}': ownership token mismatch or entry not found (stale caller or concurrent eviction)", key);
            }
        }
        catch (Exception ex)
        {
            // Contract: Release must not throw.
            logger.LogError(ex, "Failed to release idempotency key '{Key}'", key);
        }
    }

    private static string SerializeHeaders(IReadOnlyDictionary<string, StringValues> headers)
    {
        var raw = new Dictionary<string, string?[]>(headers.Count);

        foreach (var (name, values) in headers)
        {
            raw[name] = values.ToArray();
        }

        return JsonSerializer.Serialize(raw);
    }

    private static IReadOnlyDictionary<string, StringValues> DeserializeHeaders(string? headersJson)
    {
        if (headersJson is null)
        {
            return EmptyHeaders;
        }

        var rawHeaders = JsonSerializer.Deserialize<Dictionary<string, string[]>>(headersJson);

        if (rawHeaders is null)
        {
            return EmptyHeaders;
        }

        var headers = new Dictionary<string, StringValues>(rawHeaders.Count);

        foreach (var (name, values) in rawHeaders)
        {
            headers[name] = new StringValues(values);
        }

        return headers;
    }

    private sealed record ExistingRow
    {
        public required string RequestHash { get; init; }
        public int? StatusCode { get; init; }
        public string? Headers { get; init; }
        public byte[]? Body { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
