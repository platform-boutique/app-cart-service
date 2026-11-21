# Operations

## Health

`grpc.health.v1.Health/Check` on the gRPC port (`7070`) pings Redis and answers `SERVING` or `NOT_SERVING`. That is what you want for readiness but not for liveness: restarting cart pods does not fix a Redis outage and would cause a restart storm. Use the gRPC check for readiness and a TCP check on port 7070 for liveness (the probes in [Kubernetes](kubernetes.md)). The image `HEALTHCHECK` is a TCP connect to the gRPC port, so it does not check Redis.

```bash
grpcurl -plaintext localhost:7070 grpc.health.v1.Health/Check   # NOT_SERVING if Redis is down
```

## Shutdown

The host drains for at most 3 s on SIGTERM (`TelemetryExtensions.ShutdownTimeout`) and every OTLP export has a 2 s deadline, so the process exits well within 10 s even when the collector accepts connections and never answers (measured by the author, see [Observability](observability.md)). Not re-measured for this page.

## Scaling and replicas

The service is stateless and can be scaled. All state is in Redis, and `AddItem` is a non-atomic read-modify-write, so two concurrent adds for the same user can lose an update ([ADR 1](adr/0001-redis-cart-without-ttl.md)).

## Failure behaviour

| Failure | Behaviour |
|---|---|
| `REDIS_ADDR` unset | one JSON error line (`startup failed`), exit 1 |
| Redis down at start | the process starts (lazy connection), health is `NOT_SERVING`, RPCs fail with `UNAVAILABLE` until Redis is back |
| Redis slow | `DEADLINE_EXCEEDED` (`cart storage did not answer in time`) after `REDIS_TIMEOUT_MS` |
| Caller cancelled or its deadline passed | `CANCELLED` |
| Stored value cannot be parsed | `INTERNAL` (`internal error`) |
| Invalid input | `INVALID_ARGUMENT`, the message names the field and never echoes the value |
| Collector unreachable | the service keeps serving |

## Runbook

| Symptom | Check |
|---|---|
| Cart empty after adding | the frontend's cookies are `Secure` (HTTPS or `localhost` only), then Redis and the key (the raw `user_id`, no prefix) |
| `UNAVAILABLE cart storage unavailable` | Redis reachability and `REDIS_ADDR`; the Redis exception is in the server log and on the span |
| Many `DEADLINE_EXCEEDED` | Redis latency; `REDIS_TIMEOUT_MS` (50 to 30000, default 2000) |
| Carts never expire | by design, there is no TTL; wipe Redis to clear them |
| Process exits at start | `REDIS_ADDR` missing |

There is no dashboard or alert rule for this service in the repository yet (the dashboards are an open task).
