namespace IdempotencyKey.Store;

/// <param name="RequestHash">SHA256 hex digest of the bound model arguments JSON.</param>
/// <param name="Result">Null = in-progress (lock held). Non-null = completed.</param>
/// <param name="ExpiresAt">Absolute UTC expiry.</param>
public sealed record IdempotencyEntry(string RequestHash, CachedResult? Result, DateTimeOffset ExpiresAt);

public readonly record struct AcquireResult
{
    public bool IsAcquired { get; }
    public IdempotencyEntry? Entry { get; }

    private AcquireResult(bool isAcquired, IdempotencyEntry? entry)
    {
        IsAcquired = isAcquired;
        Entry = entry;
    }

    /// <summary>This request acquired the lock. Caller must call Complete or Release.</summary>
    public static AcquireResult Acquired() => new(true, null);

    /// <summary>Lock not acquired; no completed entry — in-progress or transient race. Treat as 409.</summary>
    public static AcquireResult NotAcquired() => new(false, null);

    /// <summary>Lock not acquired; a completed entry exists for replay or hash mismatch check.</summary>
    public static AcquireResult NotAcquired(IdempotencyEntry entry) => new(false, entry);
}

public interface IIdempotencyKeyStore
{
    ValueTask<AcquireResult> TryAcquire(Guid key, string requestHash, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the completed result. Only called for non-5xx responses.
    /// Implementations must not throw; exceptions must be caught and absorbed internally.
    /// </summary>
    ValueTask Complete(Guid key, CachedResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lock without storing a result. Called on 5xx or unhandled exception.
    /// Implementations must not throw; exceptions must be caught and absorbed internally.
    /// </summary>
    ValueTask Release(Guid key, CancellationToken cancellationToken);
}
