namespace IdempotencyKey.Models;

public record PaymentResponse(Guid PaymentId, decimal Amount, string Currency, string RecipientId, DateTimeOffset CreatedAt);
