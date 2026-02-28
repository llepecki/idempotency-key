using System.Collections.Concurrent;
using IdempotencyKey.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IdempotencyKey.Store;

// Thread-safety: TryAdd is the sole lock-acquisition primitive. Only one thread wins TryAdd
// for a given key; all others observe the existing entry and branch accordingly.
//
// TTL eviction: passive sweep on every TryAcquire call. Not called on Complete/Release
// to keep those paths fast.
//
// Production (Redis) differences:
//   Acquire → SET key value NX PX ttlMs
//   Complete → SET key value XX (only overwrite if key exists)
//   Release  → DEL key
//   TTL      → Redis native expiry; no eviction sweep needed
public sealed class InMemoryIdempotencyKeyStore(
    IOptions<IdempotencyKeyOptions> options,
    TimeProvider timeProvider) : IIdempotencyKeyStore
{
    private readonly ConcurrentDictionary<Guid, IdempotencyEntry> _store = new();

    public ValueTask<AcquireResult> TryAcquire(
        Guid key, string requestHash, CancellationToken cancellationToken)
    {
        EvictExpired();

        var expiresAt = timeProvider.GetUtcNow().Add(options.Value.Ttl);
        var sentinel = new IdempotencyEntry(requestHash, Result: null, expiresAt);

        if (_store.TryAdd(key, sentinel))
        {
            return ValueTask.FromResult(AcquireResult.Acquired());
        }

        if (_store.TryGetValue(key, out var existing))
        {
            // In-progress always returns 409 and should not be evicted by TTL.
            if (existing.Result is null)
            {
                return ValueTask.FromResult(AcquireResult.NotAcquired());
            }

            if (existing.ExpiresAt <= timeProvider.GetUtcNow())
            {
                _store.TryRemove(key, out _);
                // Conservative 409; client retries and re-acquires.
                return ValueTask.FromResult(AcquireResult.NotAcquired());
            }

            return ValueTask.FromResult(AcquireResult.NotAcquired(existing));
        }

        // Entry disappeared between TryAdd and TryGetValue (concurrent eviction). Safe 409.
        return ValueTask.FromResult(AcquireResult.NotAcquired());
    }

    public ValueTask Complete(Guid key, CachedResult result, CancellationToken cancellationToken)
    {
        _store.AddOrUpdate(
            key,
            addValueFactory: _ => throw new InvalidOperationException(
                $"Cannot complete idempotency key '{key}': lock entry not found."),
            updateValueFactory: (_, existing) => existing with
            {
                Result = result,
                ExpiresAt = timeProvider.GetUtcNow().Add(options.Value.Ttl)
            });
        return ValueTask.CompletedTask;
    }

    public ValueTask Release(Guid key, CancellationToken cancellationToken)
    {
        _store.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    private void EvictExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (k, entry) in _store)
        {
            if (entry.Result is not null && entry.ExpiresAt <= now)
            {
                _store.TryRemove(k, out _);
            }
        }
    }
}
