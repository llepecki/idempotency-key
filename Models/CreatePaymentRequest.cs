namespace IdempotencyKey.Models;

public record CreatePaymentRequest(decimal Amount, string Currency, string RecipientId);
