namespace IdempotencyKey.Filters;

public sealed class IdempotencyFilterException : Exception
{
    public IdempotencyFilterException(string message) : base(message) { }
    public IdempotencyFilterException(string message, Exception innerException) : base(message, innerException) { }
}
