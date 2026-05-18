using System.Collections.Concurrent;
using IdempotencyKey.Filters;
using Microsoft.Extensions.Options;

namespace IdempotencyKey.Store;

// Thread-safety: TryAdd is the sole lock-acquisition primitive. Only one thread wins TryAdd
// for a given key; all others observe the existing entry and branch accordingly.
//
// Ownership token: each successful TryAcquire generates a unique Guid. Complete and Release
// validate the token via compare-and-swap (TryUpdate) / conditional remove, preventing
// a stale request (whose sentinel expired and was re-acquired by another request) from
// corrupting the new owner's state.
//
// TTL eviction: passive sweep on every TryAcquire call. Not called on Complete/Release
// to keep those paths fast. Sentinels expire after LockTimeout (default 5 min); completed
// entries expire after Ttl (default 24h). Both are evicted uniformly by EvictExpired.
//
// Production (Redis) differences:
//   Acquire → SET key value NX PX lockTimeoutMs (value includes ownership token)
//   Complete → Lua script: check ownership token, then SET key value XX PXAT ttlMs
//   Release  → Lua script: check ownership token, then DEL key
//   TTL      → Redis native expiry; no eviction sweep needed
public sealed class InMemoryIdempotencyKeyStore(
    IOptions<IdempotencyKeyOptions> options,
    ILogger<InMemoryIdempotencyKeyStore> logger,
    TimeProvider timeProvider) : IIdempotencyKeyStore
{
    private sealed record LockEntry(Guid OwnershipToken, StoredResponse Response);

    private readonly ConcurrentDictionary<Guid, LockEntry> _store = new();

    public ValueTask<AcquireResult> TryAcquire(Guid key, string requestHash, CancellationToken cancellationToken)
    {
        EvictExpired();

        var ownershipToken = Guid.NewGuid();
        var expiresAt = timeProvider.GetUtcNow().Add(options.Value.LockTimeout);
        var sentinel = new StoredResponse(requestHash, Snapshot: null, expiresAt);
        var entry = new LockEntry(ownershipToken, sentinel);

        if (_store.TryAdd(key, entry))
        {
            return ValueTask.FromResult(AcquireResult.Acquired(ownershipToken));
        }

        if (_store.TryGetValue(key, out var existing))
        {
            if (existing.Response.ExpiresAt <= timeProvider.GetUtcNow())
            {
                _store.TryRemove(key, out _);

                // Re-acquire immediately instead of forcing a client retry.
                if (_store.TryAdd(key, entry))
                {
                    return ValueTask.FromResult(AcquireResult.Acquired(ownershipToken));
                }

                // Another thread won the race — fall through to read the new entry.
                if (_store.TryGetValue(key, out existing))
                {
                    if (!existing.Response.IsCompleted)
                    {
                        return ValueTask.FromResult(AcquireResult.NotAcquired());
                    }

                    return ValueTask.FromResult(AcquireResult.NotAcquired(existing.Response));
                }

                return ValueTask.FromResult(AcquireResult.NotAcquired());
            }

            // In-progress (sentinel): Result is null, lock not yet expired.
            if (!existing.Response.IsCompleted)
            {
                return ValueTask.FromResult(AcquireResult.NotAcquired());
            }

            return ValueTask.FromResult(AcquireResult.NotAcquired(existing.Response));
        }

        // Entry disappeared between TryAdd and TryGetValue (concurrent eviction). Safe 409.
        return ValueTask.FromResult(AcquireResult.NotAcquired());
    }

    public ValueTask Complete(Guid key, Guid ownershipToken, HttpResponseSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_store.TryGetValue(key, out var existing))
        {
            throw new IdempotencyStoreException(
                $"Can't complete idempotency key '{key}': entry not found (likely evicted or released concurrently)");
        }

        if (existing.OwnershipToken != ownershipToken)
        {
            throw new IdempotencyStoreException(
                $"Can't complete idempotency key '{key}': ownership token mismatch (stale caller)");
        }

        var completed = new LockEntry(
            ownershipToken,
            existing.Response with
            {
                Snapshot = snapshot,
                ExpiresAt = timeProvider.GetUtcNow().Add(options.Value.Ttl)
            });

        // CAS: only succeeds if the entry has not been modified since we read it.
        if (!_store.TryUpdate(key, completed, existing))
        {
            throw new IdempotencyStoreException($"Can't complete idempotency key '{key}': entry was modified concurrently");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask Release(Guid key, Guid ownershipToken, CancellationToken cancellationToken)
    {
        if (!_store.TryGetValue(key, out var existing))
        {
            return ValueTask.CompletedTask;
        }

        if (existing.OwnershipToken != ownershipToken)
        {
            logger.LogWarning("Can't release idempotency key '{Key}': ownership token mismatch (stale caller)", key);
            return ValueTask.CompletedTask;
        }

        // Atomic conditional removal: only removes if the value still matches.
        ((ICollection<KeyValuePair<Guid, LockEntry>>)_store).Remove(
            new KeyValuePair<Guid, LockEntry>(key, existing));

        return ValueTask.CompletedTask;
    }

    private void EvictExpired()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var (key, entry) in _store)
        {
            if (entry.Response.ExpiresAt <= now)
            {
                _store.TryRemove(key, out _);
            }
        }
    }
}
