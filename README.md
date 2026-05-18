# idempotency-key

A minimal ASP.NET Core 8.0 blueprint demonstrating idempotency key handling for HTTP POST requests.

> **Blueprint — not production-ready as-is.**
> This project is intentionally minimal: no external NuGet packages, in-memory store only, no authentication.
> It is designed to be read, understood, and adapted — not deployed directly.

Standards alignment: [Stripe idempotency](https://stripe.com/docs/api/idempotent_requests) and [IETF draft-ietf-httpapi-idempotency-key-header](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/).

---

## Background: what is idempotency?

An operation is **idempotent** if executing it multiple times produces the same result as executing it once. In HTTP, GET, PUT, and DELETE are naturally idempotent or safe. POST is not — submitting the same POST twice creates two resources.

### The double-submit problem

Networks are unreliable. A client sends a POST, the server processes it, and then the connection drops before the response arrives. The client has no way to know whether the request succeeded. Its options are:

- Do not retry → risk losing the operation (lost payment)
- Retry blindly → risk duplicating it (double charge)

The same scenario plays out in mobile reconnects, browser back-button submissions, load balancer retries, and automated retry loops.

### The idempotency key solution

The client generates a unique identifier (UUID) before sending the request and includes it in every attempt as the `Idempotency-Key` header. The server uses this key to de-duplicate:

- First request with a key: execute the handler, store the result.
- Subsequent requests with the same key and same payload: return the stored result without re-executing the handler.

**Core guarantee:** same `Idempotency-Key` header + same payload = identical response, handler executes exactly once.

---

## Project structure

```
IdempotencyKey.csproj
Program.cs                          — App setup, DI registration, endpoint definition
Models/
  CreatePaymentRequest.cs           — Request DTO: Amount, Currency, RecipientId
  PaymentResponse.cs                — Response DTO: PaymentId, Amount, Currency, RecipientId, CreatedAt
Filters/
  IdempotencyOptions.cs             — Configuration: Ttl (24h)
  IdempotencyFilter.cs              — Core IEndpointFilter: validation, locking, fingerprinting, replay
Store/
  IIdempotencyStore.cs              — Three-method lock protocol abstraction + StoredResponse record
  InMemoryIdempotencyStore.cs       — ConcurrentDictionary implementation with passive TTL eviction
```

---

## Quick start

```bash
dotnet run
```

The API listens on `http://localhost:5000` (or as configured in `launchSettings.json`).

### Path 1: first request — 201 Created

```bash
curl -s -i -X POST http://localhost:5000/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000" \
  -d '{"amount":100.00,"currency":"USD","recipientId":"user-123"}'
```

```
HTTP/1.1 201 Created
Location: /payments/<new-payment-id>

{"paymentId":"...","amount":100.00,"currency":"USD","recipientId":"user-123","createdAt":"..."}
```

### Path 2: replay (same key, same payload) — 201 with identical body

Run the same command again. The handler does not execute; the stored result is returned verbatim.

```
HTTP/1.1 201 Created
Location: /payments/<same-payment-id>
Idempotent-Replayed: true

{"paymentId":"...","amount":100.00,"currency":"USD","recipientId":"user-123","createdAt":"..."}
```

The `paymentId` and `createdAt` are identical to path 1 — the response is replayed, not re-generated. The `Idempotent-Replayed: true` header signals that this is a cached replay, not a fresh execution.

### Path 3: missing header — 400 Bad Request

```bash
curl -s -i -X POST http://localhost:5000/payments \
  -H "Content-Type: application/json" \
  -d '{"amount":100.00,"currency":"USD","recipientId":"user-123"}'
```

```
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"detail":"The 'Idempotency-Key' header is required.","status":400}
```

### Path 4: non-UUID key — 400 Bad Request

```bash
curl -s -i -X POST http://localhost:5000/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: my-custom-key" \
  -d '{"amount":100.00,"currency":"USD","recipientId":"user-123"}'
```

```
HTTP/1.1 400 Bad Request

{"detail":"The 'Idempotency-Key' header must be a valid UUID.","status":400}
```

### Path 5: same key, different payload — 422 Unprocessable Entity

```bash
# Use a key that was already used with amount:100
curl -s -i -X POST http://localhost:5000/payments \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000" \
  -d '{"amount":250.00,"currency":"USD","recipientId":"user-456"}'
```

```
HTTP/1.1 422 Unprocessable Entity

{"detail":"The 'Idempotency-Key' was already used with a different request payload.","status":422}
```

### Path 6: concurrent in-flight — 409 Conflict

If a second request arrives while the first is still executing (lock held, result not yet stored):

```
HTTP/1.1 409 Conflict

{"detail":"A request with this 'Idempotency-Key' is already being processed. Retry after it completes.","status":409}
```

---

## Design decisions

Each decision documents what was chosen, why, and what trade-offs or alternatives were considered.

### 5.1 `IEndpointFilter` instead of middleware

**Decision:** The idempotency logic is implemented as `IEndpointFilter`, not ASP.NET Core middleware.

**Rationale:** The filter needs to fingerprint the request payload to detect key reuse with a different body (path 6). Fingerprinting requires access to the deserialized request body. `IEndpointFilter` runs *after* model binding, so `context.Arguments` already contains the bound `CreatePaymentRequest` object — ready to serialize and hash.

Middleware runs *before* model binding. At that point only the raw `HttpContext` is available. Reading the body for hashing would require `Request.EnableBuffering()` to allow the stream to be re-read by model binding, plus a manual deserialization pass — duplicating work the framework is about to do anyway.

**Why not `IActionFilter`?** This project uses Minimal APIs, not MVC controllers. `IActionFilter` is an MVC primitive and is not applicable here. `IEndpointFilter` is the correct Minimal API equivalent.

**Trade-off:** Filters are endpoint-scoped, not global. This is a feature, not a limitation — it lets you apply idempotency selectively to the routes that need it rather than to the entire pipeline.

### 5.2 `Idempotency-Key` header name

**Decision:** The header is named exactly `Idempotency-Key`.

**Rationale:** This is the header name specified by IETF draft-ietf-httpapi-idempotency-key-header. Stripe uses the same name. Using the standardized name means clients written against this API require no changes to also work against Stripe or any other compliant server.

Inventing a proprietary header name (e.g., `X-Idempotency-Key`) would fragment the ecosystem without benefit.

### 5.3 UUID-only key format

**Decision:** The `Idempotency-Key` value must be a valid UUID (validated with `Guid.TryParse`). No separate length check is needed — UUID format validation implicitly rejects oversized inputs.

**Rationale — UUID:**
- Clients can generate UUIDs without server coordination (no round-trip to reserve a key).
- UUIDs are globally unique with negligible collision probability when generated with a CSPRNG (UUID v4).
- The character set (hex digits and hyphens) has no encoding ambiguities and is safe in HTTP headers.
- The canonical form (36 characters) has a fixed, known maximum length.

**Rationale — rejecting arbitrary strings:** Arbitrary key formats introduce injection risk (spaces, control characters, Unicode) and eliminate the uniqueness guarantee. A client that constructs keys from user input may accidentally create collisions.

### 5.4 Three-method lock protocol: TryAcquire / Complete / Release

**Decision:** `IIdempotencyStore` exposes three methods instead of a simpler Get/Set pair.

**Why Get/Set is broken:**

Consider two concurrent requests arriving with the same key for the first time:

1. Thread A calls `GetAsync(key)` → returns `null` (no entry)
2. Thread B calls `GetAsync(key)` → returns `null` (no entry, race)
3. Thread A executes the handler → creates payment #1
4. Thread B executes the handler → creates payment #2
5. Thread A calls `SetAsync(key, result-1)` → stored
6. Thread B calls `SetAsync(key, result-2)` → silently overwrites

Two payments are created. The damage is done before `SetAsync` is ever called. No amount of locking in `SetAsync` fixes this — the race is in the gap between `GetAsync` and handler execution.

**The solution — atomic lock acquisition:**

`TryAcquire` inserts a sentinel entry (`Result == null`) atomically via `ConcurrentDictionary.TryAdd`. Only one thread wins `TryAdd` for a given key; all others observe the existing entry and branch accordingly. The lock is held from the moment `TryAdd` succeeds until `Complete` or `Release` is called.

```csharp
// InMemoryIdempotencyStore.cs
if (_store.TryAdd(key, sentinel))
    return ValueTask.FromResult<(bool, StoredResponse?)>((true, null));
```

The Redis equivalent is `SET key value NX PX ttlMs` (set-if-not-exists with TTL), which provides the same atomic guarantee across processes.

**Complete:** transitions the entry from `null → HttpResponseSnapshot`. The filter captures the `IResult` into a `HttpResponseSnapshot` (status code, headers, body) before passing it to the store. The lock is relinquished; future requests see a completed result and replay it. `Complete` validates the ownership token — if the sentinel expired and was re-acquired by another request, the stale caller's `Complete` is a no-op.

**Release:** removes the entry entirely. Used on 5xx responses, unhandled exceptions, and non-cacheable returns. Removal allows the client to retry: the next attempt re-acquires the lock cleanly. `Release` validates the ownership token — stale callers can't delete a re-acquired lock.

**Lock is always released:** The filter wraps handler execution in a try/catch. Any unhandled exception calls `Release` (with the ownership token) before rethrowing, preventing a stuck lock.

```csharp
// IdempotencyFilter.cs
var ownershipToken = acquireResult.OwnershipToken;
try
{
    result = await next(context);
}
catch
{
    await store.Release(key, ownershipToken, httpContext.RequestAborted);
    throw;
}
```

### 5.5 `Result == null` as the in-progress sentinel

**Decision:** `StoredResponse.Result == null` signals "lock held, result not yet stored."

**Rationale:** A separate enum field (e.g., `Status: Pending | Completed`) would require an additional property and a more complex update operation. Using `null` leverages the existing nullable `HttpResponseSnapshot?` field as a free state bit.

`StoredResponse` is an immutable record. `Complete` uses `AddOrUpdate` with a `with` expression to transition `null → HttpResponseSnapshot` atomically:

```csharp
// InMemoryIdempotencyStore.cs
_store.AddOrUpdate(
    key,
    addValueFactory: _ => throw new InvalidOperationException(...),
    updateValueFactory: (_, existing) => existing with { Result = result });
```

The null check in the filter is a single comparison — no allocation, no boxing, no enum overhead.

### 5.6 Request fingerprinting with SHA256

**Decision:** The request is fingerprinted by SHA256-hashing the JSON-serialized bound model arguments. A mismatch between the stored hash and the incoming request hash returns 422.

**Why fingerprint at all:** Without fingerprinting, a client could reuse a key from a successfully completed request with a different payload and receive the stored result for the *original* request — a result for a request it did not make. This is either a client bug (confused retry logic) or a deliberate attack (key hijacking to replay a different user's authorized operation).

**Why SHA256:**
- Cryptographic collision resistance eliminates false negatives (two different payloads that hash identically).
- `SHA256.HashData(span)` in .NET 7+ is allocation-minimal — no intermediate `SHA256` instance, no stream wrapping.
- The hex output is a plain ASCII string, safe to store and compare with `StringComparison.Ordinal`.

**Why serialize bound arguments, not the raw body:** The request body stream is consumed by model binding before `IEndpointFilter.InvokeAsync` is called. The stream is not seekable by default; re-reading it would require `Request.EnableBuffering()` and a second deserialization pass. Serializing `context.Arguments` (the already-bound objects) is semantically equivalent for standard JSON endpoints and is available without any extra setup.

**Normalization behavior:** If model binding normalizes input (e.g., trims whitespace, case-folds enum values), the hash reflects the normalized form. Two raw-byte-different payloads that bind to the same object hash identically. This is the correct behavior — they represent the same semantic request.

**Why 422, not 400, for a hash mismatch:** The request is syntactically valid HTTP with a valid UUID key. The problem is a business-rule violation: the key has been used before with a different payload. RFC 9110 defines 422 Unprocessable Content as "the server understands the content type of the request content, and the syntax of the request content is correct, but it was unable to process the contained instructions." This matches the scenario precisely. 400 would be misleading — there is nothing wrong with the request syntax.

### 5.7 5xx responses are not cached

**Decision:** If the handler returns a 5xx result, `Release` is called (the lock is dropped) and the result is returned uncached.

**Rationale:** 5xx errors represent transient server-side failures: database unavailability, downstream timeouts, out-of-memory conditions. These are expected to be temporary. If a 500 is cached, the client can never succeed with that key — it is permanently locked out for the TTL duration, even after the underlying problem is resolved.

Releasing the lock on 5xx allows the client to retry with the same key and payload. On retry, the lock is re-acquired cleanly and the handler executes again.

**4xx responses ARE cached.** If a client submits a semantically invalid payment and receives 400 or 422, that rejection is the correct result of that request. A retry with the same payload should receive the same rejection without re-executing validation. The 4xx is the idempotent result.

**Detection:** `IStatusCodeHttpResult` (available in ASP.NET Core since .NET 7) exposes the status code of an `IResult` without executing it. The filter uses pattern matching to inspect the code before deciding whether to cache:

```csharp
// IdempotencyFilter.cs
if (result is IStatusCodeHttpResult { StatusCode: >= 500 and <= 599 })
{
    await store.Release(key, httpContext.RequestAborted);
    return result;
}
```

### 5.8 Non-`IResult` returns are not cached

**Decision:** If the handler returns a non-`IResult` value (e.g., a plain C# object), the filter releases the lock and returns the result uncached.

**Rationale:** ASP.NET Core Minimal APIs allow handlers to return plain objects (`return new PaymentResponse(...)`), which the framework serializes automatically. However, a plain `object?` reference can't be re-executed later by the filter — the filter has no way to reproduce the serialized response from a stored `object?` without duplicating framework-internal serialization logic. Attempting to cache and replay raw objects would require reflection or a custom serializer, introducing complexity and fragility.

The safe default is to release the lock: no retry protection, no replay, but also no risk of corrupting the response. This is a documented constraint.

**Solution for handlers that must be idempotent:** return `Results.Ok(value)` instead of `value` directly. An `IResult` is a replayable unit of behavior by design.

**Argument discovery:** The filter finds the request DTO using `context.Arguments.OfType<TRequest>().FirstOrDefault()` (type-based discovery), not by positional index. This means endpoint parameters can be reordered or have injected services preceding the DTO without breaking fingerprinting.

```csharp
// Works: the filter can cache and replay this
return Results.Created($"/payments/{response.PaymentId}", response);

// Does not work: filter treats this as non-cacheable
return response;
```

### 5.9 TTL of 24 hours

**Decision:** The default `IdempotencyOptions.Ttl` is 24 hours.

**Rationale:**
- Stripe's documented TTL for idempotency keys is 24 hours.
- The IETF draft recommends a TTL that covers the client's full retry window. 24 hours covers all realistic scenarios: intermittent mobile connectivity, manual retries, and automated retry loops with exponential backoff.

**Why not infinite?** Keys that never expire become a memory leak. Clients must not rely on idempotency keys being valid indefinitely; they should generate a new key for each logical operation.

**Why 24h and not shorter (e.g., 1 hour)?** A 1-hour window closes before some legitimate retry scenarios (a mobile client that goes offline for several hours and reconnects). 24 hours is the industry-established minimum for payment APIs.

**Changing the TTL in `Program.cs`:**

```csharp
builder.Services.Configure<IdempotencyOptions>(o => o.Ttl = TimeSpan.FromHours(48));
```

### 5.10 Stale lock recovery via `LockTimeout`

**Decision:** Sentinel entries (in-progress locks) are created with `ExpiresAt = now + LockTimeout` (default 5 minutes) instead of the full `Ttl`. `Complete` resets `ExpiresAt` to `now + Ttl` when transitioning sentinel to completed, so completed entries retain the full 24-hour window.

**Problem:** If a request acquires the lock (inserts a sentinel with `Result == null`) and then the process crashes, the handler hangs, or `Release` is never called, the sentinel is never cleaned up. Without recovery, the key is permanently stuck returning 409 Conflict.

**Solution:** By giving sentinels a short expiry, the existing expiry-based eviction naturally cleans them up. No special-case logic or separate background process is needed. A stale sentinel expires after `LockTimeout`, is evicted by the next `TryAcquire` sweep, and the client's retry re-acquires the lock cleanly.

**Why 5 minutes?** Long enough that a slow-but-healthy handler does not have its lock stolen mid-execution. Short enough that a crashed process unblocks retries in a reasonable timeframe. Configurable via `IdempotencyKeyOptions.LockTimeout`.

### 5.11 Passive TTL eviction (no background timer)

**Decision:** Expired entries are swept on every `TryAcquire` call. There is no background `IHostedService` or timer.

**Rationale:** A background timer introduces concurrency concerns: the timer could evict an entry that is currently being completed by a request thread. `TryRemove` in the eviction loop and `AddOrUpdate` in `Complete` would race, requiring careful ordering or an explicit check after eviction. The passive approach eliminates this class of bug entirely — eviction only happens at the point where a new lock is being requested, which is the safest moment to clean up.

**How it works:**

```csharp
// InMemoryIdempotencyStore.cs
private void EvictExpired()
{
    var now = DateTimeOffset.UtcNow;
    foreach (var (k, entry) in _store)
        if (entry.ExpiresAt <= now)
            _store.TryRemove(k, out _);
}
```

`EvictExpired` is called at the top of `TryAcquire` — before the new sentinel is inserted. This ensures the store does not accumulate unbounded expired entries as long as new requests continue to arrive. Both stale sentinels (expired after `LockTimeout`) and completed entries (expired after `Ttl`) are evicted uniformly.

**Not called in `Complete` or `Release`:** These are on the hot path (called immediately after handler execution). Eviction is a best-effort cleanup concern; adding an O(n) sweep to every response would increase tail latency unnecessarily.

**Trade-off:** Memory is not reclaimed between `TryAcquire` calls. In a low-traffic API with a long TTL, entries can accumulate. For production with high key volume, a background `IHostedService` sweeping every few minutes, or a Redis-backed store with native TTL expiry, is more appropriate.

### 5.12 `IIdempotencyStore` registered as a singleton

**Decision:** `IIdempotencyStore` is registered with `AddSingleton`.

**Rationale:** The store is shared application state. It must persist across requests and outlive any individual HTTP request scope. A scoped registration would create a new (empty) store instance per request, making every request see an empty store — the idempotency guarantee would never be enforced.

**Implication for custom implementations:** Any class implementing `IIdempotencyStore` must be fully thread-safe. The in-memory implementation uses `ConcurrentDictionary` and relies on its atomic `TryAdd` and `AddOrUpdate` guarantees. A Redis implementation would rely on Redis's single-threaded command execution model for atomicity.

### 5.13 RFC 9457 ProblemDetails for all error responses

**Decision:** All error responses use `Results.Problem(detail, statusCode)`, producing `application/problem+json` bodies per RFC 9457.

**Rationale:** A consistent error format allows clients to reliably parse `status` and `detail` fields regardless of which validation branch rejected the request. Clients do not need to handle both plain-text errors and JSON errors from the same endpoint.

`Program.cs` configures the full ProblemDetails pipeline:

```csharp
builder.Services.AddProblemDetails();

app.UseExceptionHandler();
app.UseStatusCodePages();
```

`UseExceptionHandler()` converts unhandled exceptions to ProblemDetails responses. `UseStatusCodePages()` ensures that status-only responses (e.g., a 404 from a missing route) also produce a ProblemDetails body rather than an empty response.

---

## Production adaptation guide

### 6.1 Per-user scoping (required for security)

This blueprint uses the raw `Idempotency-Key` value as the store key. In a production API with authentication, this allows any authenticated user to replay another user's request by submitting the same key.

The fix is to prefix the store key with the authenticated user's identity:

```csharp
// Not implemented in this blueprint — no authentication.
// Add this before calling store.TryAcquire:
var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? throw new InvalidOperationException("Authenticated user required.");
var scopedKey = $"{userId}:{key}";
```

With scoping, `user-A:550e8400-...` and `user-B:550e8400-...` are independent entries. User B can't observe or replay User A's result.

### 6.2 Redis-backed store

The in-memory store does not survive process restarts and does not coordinate between multiple API instances. A horizontally scaled deployment requires a shared, distributed store.

Redis is the standard choice. The three-method protocol maps directly to Redis commands:

| Operation | In-memory | Redis |
|---|---|---|
| `TryAcquire` | `ConcurrentDictionary.TryAdd` (ExpiresAt = `now + LockTimeout`) | `SET key value NX PX lockTimeoutMs` |
| `Complete` | `AddOrUpdate` (update only; resets ExpiresAt to `now + Ttl`) | `SET key value XX PXAT ttlMs` |
| `Release` | `TryRemove` | `DEL key` |
| TTL eviction | Passive sweep in `TryAcquire` (all expired entries) | Redis native expiry |
| Distributed | No | Yes |
| Persistent across restarts | No | Yes (with persistence enabled) |

`SET key value NX PX ttlMs` is atomic at the Redis server: it sets the key only if it does not exist (`NX`) and applies the TTL in the same command (`PX ttlMs`). This is the distributed equivalent of `ConcurrentDictionary.TryAdd`.

`SET key value XX` updates the key only if it already exists, preventing a `Complete` from creating an orphan entry if the lock was concurrently evicted.

To implement: create a class `RedisIdempotencyStore : IIdempotencyStore` using `StackExchange.Redis` and register it as the singleton in `Program.cs`. The `IdempotencyFilter` and the rest of the application are unaffected.

### 6.3 Response header signalling replays

When a cached result is replayed, the response includes the `Idempotent-Replayed: true` header. This follows the Stripe convention and allows clients to distinguish a fresh response from a cached replay.

The header is set in the replay branch of `IdempotencyKeyFilter.InvokeAsync`:

```csharp
// IdempotencyKeyFilter.cs
httpContext.Response.Headers["Idempotent-Replayed"] = "true";
return acquireResult.Entry.Result; // Replay cached result.
```

---

## Intentional omissions

The following are deliberately absent from this blueprint. Each is a real production concern but would obscure the core idempotency logic without adding clarity to the reference implementation.

| Omission | Why excluded |
|---|---|
| Authentication and authorization | Requires an auth stack (JWT, cookies, OAuth). Adds complexity unrelated to idempotency mechanics. |
| Per-user key scoping | Depends on authentication. The pattern is documented in section 6.1. |
| Persistence across restarts | The in-memory store is the simplest correct implementation. Redis adaptation is in section 6.2. |
| Horizontal scaling / distributed locking | Same as persistence — requires Redis or equivalent. |
| Background TTL eviction timer | Adds concurrency complexity. Passive eviction is correct for the single-process case. |
| Observability (metrics, structured logs for replays) | Application-specific. Hooks belong in `IdempotencyFilter` at the replay and cache branches. |
| Non-JSON / stream-based response support | `IResult` is the only replayable unit available to the filter. Stream responses can't be cached without buffering. |

---

## Standards references

- [Stripe: Idempotent Requests](https://stripe.com/docs/api/idempotent_requests) — industry reference for the 24h TTL, 255-char key limit, and UUID key format recommendation.
- [IETF draft-ietf-httpapi-idempotency-key-header](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) — the standardization effort for the `Idempotency-Key` header name and semantics.
- [RFC 9457 — Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457) — defines `application/problem+json` used for all error responses.
- [RFC 9110 — HTTP Semantics](https://www.rfc-editor.org/rfc/rfc9110) — defines 422 Unprocessable Content (section 15.5.21), used for key reuse with a mismatched payload.
