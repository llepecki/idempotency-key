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
             b. Body > MaxCachedResponseBodySize → Release; return result (not cached)
             c. 5xx result             → Release; return 5xx (client can retry)
             d. non-5xx IResult        → Complete
                i.  Complete succeeded  → return result
                ii. Complete failed     → Release; return 500 (persistence failure)
             e. non-IResult            → Release; throw InvalidOperationException
        4. TryAcquire → not acquired, entry is null  → 409 Conflict (in-progress)
        5. TryAcquire → not acquired, hash mismatch  → 422 Unprocessable Entity
        6. TryAcquire → not acquired, hash match     → replay cached HttpResponseSnapshot
```

### Key Components

- **`Filters/IdempotencyKeyOptions.cs`** — Configuration. `Ttl` (default 24h), `LockTimeout` (default 5 min), `MaxCachedResponseBodySize` (default 1 MB). Registered via `AddOptions<IdempotencyKeyOptions>()`.
- **`Filters/IdempotencyKeyFilter.cs`** — Core logic. Implements `IEndpointFilter`. Validates key format (UUID), discovers the request DTO by type (`context.Arguments.OfType<TRequest>()`), fingerprints requests (SHA256 of bound model arguments), enforces the full decision tree above. Captures `IResult` via body-swap on the real `HttpContext` (swaps `Response.Body` with a `MemoryStream`, executes the result, captures status/headers/body, restores the original stream).
- **`Store/IIdempotencyKeyStore.cs`** — Three-method lock protocol abstraction: `TryAcquire`, `Complete`, `Release`. Defines `StoredResponse` record and `AcquireResult` (includes `OwnershipToken`). `Complete` returns `ValueTask` (void) and throws on any failure — ownership token mismatch, concurrent modification, or infrastructure errors; the filter catches exceptions, releases the lock, and returns 500. `Release` requires the ownership token and must not throw; stale callers are silently ignored.
- **`Store/InMemoryIdempotencyKeyStore.cs`** — Singleton `ConcurrentDictionary<Guid, LockEntry>` implementation (where `LockEntry` wraps `OwnershipToken` + `StoredResponse`). `Complete` uses CAS (`TryUpdate`), `Release` uses atomic conditional removal. Passive TTL eviction on every `TryAcquire`.
- **`Program.cs`** — Registers options, `IIdempotencyKeyStore` as singleton, applies `IdempotencyKeyFilter` to the `/payments` endpoint group.
- **`Models/`** — `CreatePaymentRequest` and `PaymentResponse` record types (DTOs).

### Lock Protocol

`StoredResponse.Snapshot == null` is the in-progress sentinel. Each lock is tagged with an `OwnershipToken` (UUID) generated on acquire. `Complete` and `Release` validate this token — if a sentinel expires and is re-acquired by a different request, the original (stale) caller's `Complete`/`Release` is a no-op.

| State | Snapshot field | OwnershipToken | ExpiresAt | Meaning |
|---|---|---|---|---|
| Lock acquired (first request) | `null` | unique Guid | `now + LockTimeout` | Caller must call `Complete` or `Release` with this token |
| In-progress (concurrent request) | `null` | (other owner's) | `now + LockTimeout` | Another request is executing → 409 |
| Stale sentinel (crash/hang) | `null` | (expired owner's) | expired | Evicted on next `TryAcquire`; client retries re-acquire |
| Completed | non-`null` | (original owner's) | `now + Ttl` | Result available for replay |

The lock is never held after the response is written: `Complete` transitions `null → HttpResponseSnapshot` (with ownership token validation) and resets `ExpiresAt` to `now + Ttl`; `Release` removes the entry entirely (with ownership token validation), allowing a future retry to re-acquire. If `Complete` throws, the filter releases the lock and returns 500 to the client — hard-guaranteeing that a success response is never returned without persisting the idempotency record.

### TTL Behaviour

- Default TTL is **24 hours** (aligned with Stripe and the IETF draft).
- Default `LockTimeout` is **5 minutes** — sentinels (in-progress locks) expire after this duration. If a process crashes or the handler hangs without calling `Complete`/`Release`, the sentinel becomes eligible for eviction, unblocking future retries. `Complete` resets `ExpiresAt` to `now + Ttl`, so completed entries retain the full 24h window.
- Expiry is stored per-entry as `ExpiresAt` (absolute UTC).
- Eviction is **passive**: expired entries (both sentinels and completed) are swept on every `TryAcquire` call. There is no background timer.
- Default `MaxCachedResponseBodySize` is **1 MB**. Responses exceeding this limit are returned normally but not persisted — the lock is released, allowing the client to retry.
- To change the TTL, lock timeout, or max cached body size in `Program.cs`:
  ```csharp
  builder.Services.Configure<IdempotencyKeyOptions>(o =>
  {
      o.Ttl = TimeSpan.FromHours(48);
      o.LockTimeout = TimeSpan.FromMinutes(10);
      o.MaxCachedResponseBodySize = 2_097_152; // 2 MB
  });
  ```

### Design Constraints to Keep in Mind

- `IIdempotencyKeyStore` is registered as a **singleton** — any new implementation must be thread-safe.
- The filter captures `IResult` responses into `HttpResponseSnapshot` (status code + headers + body) before passing them to the store. Handlers returning non-`IResult` types cause the filter to release the lock and throw `InvalidOperationException`. This is a fail-fast design: applying the filter signals intent to cache, so a non-cacheable return type is treated as a programmer error.
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
| Acquire lock | `ConcurrentDictionary.TryAdd` (ExpiresAt = `now + LockTimeout`) | `SET key value NX PX lockTimeoutMs` (value includes ownership token) |
| Complete | `TryUpdate` with CAS (validates ownership token, resets ExpiresAt to `now + Ttl`) | Lua script: check ownership token, then `SET key value XX PXAT ttlMs` |
| Release | Atomic conditional remove (validates ownership token) | Lua script: check ownership token, then `DEL key` |
| TTL eviction | Passive sweep in `TryAcquire` (all expired entries) | Redis native expiry |
| Distributed | No | Yes |
| Persistent across restarts | No | Yes (with persistence enabled) |
