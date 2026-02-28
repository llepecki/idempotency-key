namespace IdempotencyKey.Filters;

public sealed class IdempotencyKeyOptions
{
    /// <summary>
    /// How long a key and its cached result are retained.
    /// Default: 24 hours (Stripe / IETF draft recommendation).
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);
}
