# 1. Carts in Redis, one key per user id, no TTL

Status: accepted (reconstructed from the code and the earlier README).

## Context

Carts are small, belong to a session or an account, and are read on almost every page of the shop. The service should be stateless so that it scales horizontally.

## Decision

Store each cart in Redis under the raw `user_id` (no key prefix) as a protobuf-serialised `Cart`, through `IDistributedCache`. `AddItem` reads, modifies and writes the cart; `EmptyCart` writes an empty cart instead of deleting the key. No TTL is set. Redis is connected lazily and every cart RPC is bounded by `REDIS_TIMEOUT_MS`.

## Consequences

- The service scales horizontally; all state is in Redis.
- Carts live until Redis is wiped, and nothing cleans up abandoned ones.
- `AddItem` is not atomic: two concurrent adds for the same user can lose an update.
- The health check pings Redis, so it is a readiness signal and not a liveness one.
