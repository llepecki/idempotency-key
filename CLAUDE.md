# CLAUDE.md

This project is a **blueprint** demonstrating idempotency key handling per established standards (Stripe, IETF draft-ietf-httpapi-idempotency-key-header). It is intentionally minimal — no external NuGet packages, in-memory store only.

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build       # Build the project
dotnet run         # Run the API (listens on http/https ports configured in launchSettings)
dotnet test        # Run tests (no test project exists yet)
```

## Architecture

This is a minimal ASP.NET Core 8.0 Web API demonstrating idempotency handling for HTTP POST requests. No external NuGet packages are used — only built-in ASP.NET Core primitives.

### Request Flow

```
POST /payments (with Idempotency-Key header)
    → IdempotencyKeyFilter (IEndpointFilter)
        1. Missing header              → 400 Bad Request
        2. Key is not a valid UUID     → 400 Bad Request
        3. TryAcquire → acquired  → execute handler
             a. Handler throws         → Release; rethrow
             b. 5xx result             → Release; return 5xx (client can retry)
             c. non-5xx IResult        → Complete; return result
             d. non-IResult            → Release; return result (cannot cache)
        4. TryAcquire → not acquired, entry is null  → 409 Conflict (in-progress)
        5. TryAcquire → not acquired, hash mismatch  → 422 Unprocessable Entity
        6. TryAcquire → not acquired, hash match     → replay cached CachedResult
```

### Key Components

- **`Filters/IdempotencyKeyOptions.cs`** — Configuration. `Ttl` (default 24h). Registered via `AddOptions<IdempotencyKeyOptions>()`.
- **`Filters/IdempotencyKeyFilter.cs`** — Core logic. Implements `IEndpointFilter`. Validates key format (UUID), fingerprints requests (SHA256 of bound model arguments), enforces the full decision tree above.
- **`Store/IIdempotencyKeyStore.cs`** — Three-method lock protocol abstraction: `TryAcquire`, `Complete`, `Release`. Defines `IdempotencyEntry` record.
- **`Store/InMemoryIdempotencyKeyStore.cs`** — Singleton `ConcurrentDictionary<string, IdempotencyEntry>` implementation. Passive TTL eviction on every `TryAcquire`.
- **`Program.cs`** — Registers options, `IIdempotencyKeyStore` as singleton, applies `IdempotencyKeyFilter` to the `/payments` endpoint group.
- **`Models/`** — `CreatePaymentRequest` and `PaymentResponse` record types (DTOs).

### Lock Protocol

`IdempotencyEntry.Result == null` is the in-progress sentinel:

| State | Result field | Meaning |
|---|---|---|
| Lock acquired (first request) | — | Caller must call `Complete` or `Release` |
| In-progress (concurrent request) | `null` | Another request is executing → 409 |
| Completed | non-`null` | Result available for replay |

The lock is never held after the response is written: `Complete` transitions `null → CachedResult`; `Release` removes the entry entirely (allowing a future retry to re-acquire).

### TTL Behaviour

- Default TTL is **24 hours** (aligned with Stripe and the IETF draft).
- Expiry is stored per-entry as `ExpiresAt` (absolute UTC).
- Eviction is **passive**: expired entries are swept on every `TryAcquire` call. There is no background timer.
- To change the TTL in `Program.cs`:
  ```csharp
  builder.Services.Configure<IdempotencyKeyOptions>(o => o.Ttl = TimeSpan.FromHours(48));
  ```

### Design Constraints to Keep in Mind

- `IIdempotencyKeyStore` is registered as a **singleton** — any new implementation must be thread-safe.
- The filter captures `IResult` responses into `CachedResult` (status code + headers + body) before passing them to the store. Handlers returning non-`IResult` types bypass caching (lock is released; they are treated as non-cacheable).
- 5xx responses are **never cached** — they are considered transient failures; the client can retry with the same key.
- Current in-memory store is not persistent across restarts and does not support horizontal scaling.
- Request fingerprinting uses SHA256 of JSON-serialized bound model arguments. This is semantically equivalent to hashing the raw request body for standard JSON endpoints, because model binding has already consumed and deserialized the body before the filter runs.

### Per-User Scoping (production pattern — not implemented here)

In production, idempotency keys must be scoped to the authenticated user to prevent one user from replaying another user's requests:

```csharp
// Example: prefix the store key with the authenticated user ID
var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? throw new InvalidOperationException("Authenticated user required.");
var scopedKey = $"{userId}:{key}";
```

This blueprint omits authentication and uses the raw key as-is.

### Redis-Backed Store (production pattern — not implemented here)

| Operation | In-memory | Redis |
|---|---|---|
| Acquire lock | `ConcurrentDictionary.TryAdd` | `SET key value NX PX ttlMs` |
| Complete | `AddOrUpdate` (update only) | `SET key value XX` |
| Release | `TryRemove` | `DEL key` |
| TTL eviction | Passive sweep in `TryAcquire` | Redis native expiry |
| Distributed | No | Yes |
| Persistent across restarts | No | Yes (with persistence enabled) |
