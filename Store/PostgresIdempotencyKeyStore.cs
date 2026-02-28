using System.Text.Json;
using Dapper;
using IdempotencyKey.Filters;
using Microsoft.Extensions.Options;
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
/// <c>Complete</c> receives a <see cref="CachedResult"/> (already captured by the filter)
/// and serializes its properties for storage. The store has no HTTP concerns.
/// </para>
/// </summary>
public sealed class PostgresIdempotencyKeyStore(
    NpgsqlDataSource dataSource,
    IOptions<IdempotencyKeyOptions> options,
    ILogger<PostgresIdempotencyKeyStore> logger) : IIdempotencyKeyStore
{
    private readonly IdempotencyKeyOptions _options = options.Value;

    // Bounded eviction: LIMIT 100 caps the work per eviction run so a large backlog of
    // expired entries doesn't spike latency on a single request. FOR UPDATE SKIP LOCKED
    // prevents concurrent eviction runs from contending on the same rows — if two requests
    // both trigger eviction simultaneously, they clean up different rows without blocking.
    // Only completed entries (status_code IS NOT NULL) are evicted; in-progress sentinels
    // are left alone since they represent active locks.
    private const string EvictionSql =
        """
        DELETE FROM idempotency_keys
        WHERE key IN (
            SELECT key FROM idempotency_keys
            WHERE expires_at < now() AND status_code IS NOT NULL
            LIMIT 100
            FOR UPDATE SKIP LOCKED
        )
        """;

    private const string InsertSql =
        """
        INSERT INTO idempotency_keys (key, request_hash, status_code, headers, body, expires_at)
        VALUES (@Key, @RequestHash, NULL, NULL, NULL, now() + @Ttl)
        ON CONFLICT (key) DO NOTHING
        RETURNING key
        """;

    private const string SelectExistingSql =
        """
        DELETE FROM idempotency_keys WHERE key = @Key AND expires_at < now();
        SELECT request_hash RequestHash, status_code StatusCode, headers Headers, body Body, expires_at ExpiresAt
        FROM idempotency_keys
        WHERE key = @Key
        """;

    public async ValueTask<AcquireResult> TryAcquire(
        Guid key, string requestHash, CancellationToken cancellationToken)
    {
        await using var connection = dataSource.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        if (Random.Shared.Next(10) == 0)
        {
            await connection.ExecuteAsync(EvictionSql);
        }

        var insertedKey = await connection.ExecuteScalarAsync<Guid?>(
            InsertSql,
            new { Key = key, RequestHash = requestHash, Ttl = _options.Ttl });

        if (insertedKey is not null)
        {
            return AcquireResult.Acquired();
        }

        // Conflict — delete if expired, then read the surviving row (if any).
        // The DELETE commits before the SELECT runs (autocommit). Under READ COMMITTED
        // the SELECT sees the deletion, so an expired row won't be returned.
        var row = await connection.QuerySingleOrDefaultAsync<ExistingRow>(
            SelectExistingSql,
            new { Key = key });

        if (row?.StatusCode is null)
        {
            return AcquireResult.NotAcquired();
        }

        var headers = row.Headers is not null
            ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(row.Headers)
              ?? new Dictionary<string, string[]>()
            : new Dictionary<string, string[]>();

        var cachedResult = new CachedResult(row.StatusCode.Value, headers, row.Body ?? []);

        var entry = new IdempotencyEntry(
            row.RequestHash,
            cachedResult,
            row.ExpiresAt);

        return AcquireResult.NotAcquired(entry);
    }

    public async ValueTask Complete(Guid key, CachedResult result, CancellationToken cancellationToken)
    {
        try
        {
            var headersJson = JsonSerializer.Serialize(result.Headers);

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
            await connection.ExecuteAsync(
                """
                UPDATE idempotency_keys
                SET status_code = @StatusCode,
                    headers     = @Headers::json,
                    body        = @Body,
                    expires_at  = now() + @Ttl
                WHERE key = @Key
                """,
                new
                {
                    Key = key,
                    StatusCode = result.StatusCode,
                    Headers = headersJson,
                    Body = result.Body,
                    Ttl = _options.Ttl
                });
        }
        catch (Exception ex)
        {
            // Contract: Complete must not throw.
            logger.LogError(ex, "Failed to complete idempotency key '{Key}'", key);
        }
    }

    public async ValueTask Release(Guid key, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = dataSource.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(
                "DELETE FROM idempotency_keys WHERE key = @Key",
                new { Key = key });
        }
        catch (Exception ex)
        {
            // Contract: Release must not throw.
            logger.LogError(ex, "Failed to release idempotency key '{Key}'", key);
        }
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
