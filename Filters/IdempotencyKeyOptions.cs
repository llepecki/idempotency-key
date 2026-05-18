using Microsoft.Extensions.Options;

namespace IdempotencyKey.Filters;

public sealed record IdempotencyKeyOptions
{
    /// <summary>
    /// How long a key and its cached result are retained.
    /// Default: 24 hours (Stripe / IETF draft recommendation).
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an in-progress sentinel (lock) is retained before it is
    /// considered stale and eligible for eviction. If a process crashes or
    /// the handler hangs without calling Complete/Release, the sentinel
    /// expires after this duration, unblocking future retries.
    /// <see cref="Complete"/> resets <c>ExpiresAt</c> to <c>now + Ttl</c>,
    /// so completed entries are unaffected by this shorter window.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Inverse probability of running passive eviction on each <c>TryAcquire</c> call.
    /// A value of N means eviction runs ~1/N of the time (e.g. 10 → ~10% of requests).
    /// Only relevant for the Postgres store; the in-memory store evicts on every call.
    /// Default: 10.
    /// </summary>
    public int EvictionRate { get; init; } = 10;

    /// <summary>
    /// Maximum response body size (in bytes) that will be cached for idempotency replay.
    /// Responses exceeding this limit are returned normally but not persisted — the lock
    /// is released, allowing the client to retry.
    /// Default: 1 MB.
    /// </summary>
    public long MaxCachedResponseBodySize { get; init; } = 1_048_576;
}

public sealed class IdempotencyKeyOptionsValidator : IValidateOptions<IdempotencyKeyOptions>
{
    public ValidateOptionsResult Validate(string? _, IdempotencyKeyOptions options)
    {
        if (options.Ttl <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Ttl must be greater than zero");
        }

        if (options.LockTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("LockTimeout must be greater than zero");
        }

        if (options.EvictionRate <= 0)
        {
            return ValidateOptionsResult.Fail("EvictionRate must be greater than zero");
        }

        if (options.MaxCachedResponseBodySize <= 0)
        {
            return ValidateOptionsResult.Fail("MaxCachedResponseBodySize must be greater than zero");
        }

        return ValidateOptionsResult.Success;
    }
}
