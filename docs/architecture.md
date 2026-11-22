# Architecture

## Context

```mermaid
flowchart LR
    fe[frontend] -- "AddItem, GetCart, EmptyCart :7070" --> cart[cart-service]
    co[checkout-service] -- "GetCart, EmptyCart" --> cart
    cart -- "protobuf Cart per user id" --> redis[(cart-redis)]
    cart -. "OTLP gRPC, optional" .-> otel[OTLP collector]
    cart -. "profiles (PYROSCOPE_ADDR), optional" .-> pyro[Pyroscope]
```

cart-service is stateless: all state is in Redis, one key per user (session) id with a protobuf-serialised `Cart` as the value. It calls no other service. The callers were checked in the `origin/main` code of the frontend (`cmd/server/rpc.go`) and checkout-service (`GetCart` and `EmptyCart`) on 2026-11-21.

## Request path

```mermaid
sequenceDiagram
    participant C as caller
    participant S as cart-service
    participant R as Redis
    C->>S: AddItem(user_id, item)
    Note over S: validate (non-empty ids up to 128 chars, quantity 1-100, total per product at most 1000)
    S->>R: GET user_id
    S->>R: SET user_id (read-modify-write, not atomic)
    S-->>C: Empty
```

Both Redis round trips of one `AddItem` share the budget `REDIS_TIMEOUT_MS` (default 2000 ms); the caller's own deadline still wins when it is shorter. Storage errors are mapped to generic gRPC statuses without the Redis exception text (see [API](api.md)).

## Components

| File | Role |
|---|---|
| `src/Program.cs` | DI, Redis cache, gRPC with reflection, JSON console logs, OpenTelemetry, endpoints |
| `src/services/CartService.cs`, `CartValidation.cs` | the handlers and the input rules |
| `src/services/HealthCheckService.cs` | `grpc.health.v1`, `NOT_SERVING` while Redis does not answer a ping |
| `src/store/` | `ICartStore` and the `IDistributedCache`-backed `RedisCartStore`, the timeout option |
| `container/entrypoint.sh`, `src/ProfilingConfig.cs` | the image entrypoint that loads the Pyroscope .NET profiler only when `PYROSCOPE_ADDR` is valid, and the same rule as a C# function for the `starting` log line |
| `src/JsonLog*.cs`, `ServiceIdentity.cs`, `TelemetryConfig.cs`, `TelemetryExtensions.cs`, `RequestIdInterceptor.cs` | one JSON log shape, service identity, telemetry contract and export deadlines, request id into the logging scope |
| `src/HealthProbe.cs` | `cartservice healthcheck`: the container HEALTHCHECK (TCP connect, no shell needed) |
| `src/protos/cart.proto` | the service contract (package `hipstershop`) |

## Decisions

[ADR 1](adr/0001-redis-cart-without-ttl.md), [ADR 2](adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md), [ADR 3](adr/0003-proto-package-stays-hipstershop.md).
