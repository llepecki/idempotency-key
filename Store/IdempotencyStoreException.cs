namespace IdempotencyKey.Store;

public sealed class IdempotencyStoreException : Exception
{
    public IdempotencyStoreException(string message) : base(message) { }
    public IdempotencyStoreException(string message, Exception innerException) : base(message, innerException) { }
}
