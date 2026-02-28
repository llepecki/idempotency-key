using IdempotencyKey.Filters;
using IdempotencyKey.Models;
using IdempotencyKey.Store;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();

// Idempotency options — defaults: 24h TTL, 255-char max key.
// Override: builder.Services.Configure<IdempotencyKeyOptions>(o => o.Ttl = TimeSpan.FromHours(48));
builder.Services.AddOptions<IdempotencyKeyOptions>();

builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("IdempotencyStore")
    ?? throw new InvalidOperationException("Connection string 'IdempotencyStore' not found.");

// Auto-prepared statements: after a connection executes the same SQL text twice
// (AutoPrepareMinUsages=2), Npgsql creates a server-side prepared statement for it.
// Subsequent executions skip the PostgreSQL parse/plan phases entirely. The store uses
// ~4 distinct SQL statements that repeat on every request — all benefit from this.
// MaxAutoPrepare=20 is generous headroom (per-connection LRU cache of prepared statements).
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.ConnectionStringBuilder.MaxAutoPrepare = 20;
dataSourceBuilder.ConnectionStringBuilder.AutoPrepareMinUsages = 2;
builder.Services.AddSingleton(dataSourceBuilder.Build());

builder.Services.AddSingleton<IIdempotencyKeyStore, PostgresIdempotencyKeyStore>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

var payments = app.MapGroup("/payments").AddEndpointFilter<IdempotencyKeyFilter<CreatePaymentRequest>>();

payments.MapPost("/", (CreatePaymentRequest req) =>
{
    var response = new PaymentResponse(Guid.NewGuid(), req.Amount, req.Currency, req.RecipientId, DateTimeOffset.UtcNow);
    return Results.Created($"/payments/{response.PaymentId}", response);
});

app.Run();
