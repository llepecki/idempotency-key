namespace IdempotencyKey.Store;

/// <param name="RequestHash">SHA256 hex digest of the bound model arguments JSON.</param>
/// <param name="Snapshot">Null = in-progress (lock held). Non-null = completed.</param>
/// <param name="ExpiresAt">Absolute UTC expiry.</param>
public sealed record StoredResponse(string RequestHash, HttpResponseSnapshot? Snapshot, DateTimeOffset ExpiresAt)
{
    public bool IsCompleted => Snapshot is not null;
}

public readonly record struct AcquireResult
{
    public bool IsAcquired { get; }

    /// <summary>
    /// Opaque token identifying the lock owner. Must be passed to <see cref="IIdempotencyKeyStore.Complete"/>
    /// and <see cref="IIdempotencyKeyStore.Release"/> to prove ownership. Only meaningful when
    /// <see cref="IsAcquired"/> is <c>true</c>; <see cref="Guid.Empty"/> otherwise.
    /// </summary>
    public Guid OwnershipToken { get; }

    public StoredResponse? Response { get; }

    private AcquireResult(bool isAcquired, Guid ownershipToken, StoredResponse? response)
    {
        IsAcquired = isAcquired;
        OwnershipToken = ownershipToken;
        Response = response;
    }

    /// <summary>This request acquired the lock. Caller must call Complete or Release.</summary>
    public static AcquireResult Acquired(Guid ownershipToken) => new(true, ownershipToken, null);

    /// <summary>Lock not acquired; no completed entry — in-progress or transient race. Treat as 409.</summary>
    public static AcquireResult NotAcquired() => new(false, Guid.Empty, null);

    /// <summary>Lock not acquired; a completed entry exists for replay or hash mismatch check.</summary>
    public static AcquireResult NotAcquired(StoredResponse response) => new(false, Guid.Empty, response);
}

public interface IIdempotencyKeyStore
{
    ValueTask<AcquireResult> TryAcquire(Guid key, string requestHash, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the completed result. Only called for non-5xx responses.
    /// Implementations must verify <paramref name="ownershipToken"/> matches the lock owner.
    /// Throws on any failure — ownership token mismatch (stale caller), concurrent modification,
    /// or infrastructure errors (e.g. DB errors). The caller catches exceptions, releases the lock,
    /// and returns 500.
    /// </summary>
    ValueTask Complete(Guid key, Guid ownershipToken, HttpResponseSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the lock without storing a result. Called on 5xx or unhandled exception.
    /// Implementations must verify <paramref name="ownershipToken"/> matches the lock owner;
    /// stale callers are silently ignored.
    /// Implementations must not throw; exceptions must be caught and absorbed internally.
    /// </summary>
    ValueTask Release(Guid key, Guid ownershipToken, CancellationToken cancellationToken);
}
