# cart-service

A gRPC service that manages shopping carts for the platform-demo e-commerce platform. Carts are stored in Redis keyed by user (session) id. The service itself is stateless, so it can be scaled horizontally; all state lives in `cart-redis`.

Part of the [platform-demo](https://github.com/platform-boutique) platform — microservices, observability (LGTM stack), GitOps (Argo CD), policy enforcement (Kyverno), infrastructure provisioning (Crossplane) and an internal developer portal (Backstage). See the master `README.md` in the platform-demo root for the whole-system view.

## At a glance

| | |
|---|---|
| **Language / runtime** | C# / ASP.NET Core on .NET 10, gRPC (`Grpc.AspNetCore`) |
| **Image** | `mladenovskistefan/cart-service` |
| **Protocol / port** | gRPC (HTTP/2 cleartext) on `7070` |
| **Metrics** | Pushed over OTLP/gRPC (OpenTelemetry .NET) → Alloy → Mimir; no scrape endpoint |
| **Health** | Standard `grpc.health.v1.Health/Check` on `7070` — **reports `NOT_SERVING` when Redis is unreachable** |
| **State** | Redis (`cart-redis:6379`). Service is stateless |
| **Depends on** | Redis (hard runtime dependency); optional OTLP collector |
| **Called by** | `frontend` (all cart operations), `checkout-service` (`GetCart`, `EmptyCart`) |
| **gRPC reflection** | Enabled — `grpcurl` works without `-proto` |

## API

Package `hipstershop`, service `CartService` ([`src/protos/cart.proto`](src/protos/cart.proto)).

| Method | Request | Response | Behaviour |
|---|---|---|---|
| `AddItem` | `AddItemRequest{ user_id, CartItem{ product_id, quantity } }` | `Empty` | Creates the cart if absent; otherwise adds `quantity` to an existing line or appends a new one |
| `GetCart` | `GetCartRequest{ user_id }` | `Cart{ user_id, repeated CartItem items }` | Returns the cart, or an empty cart if none exists |
| `EmptyCart` | `EmptyCartRequest{ user_id }` | `Empty` | Overwrites the cart with an empty one |

Input rules (violations are `INVALID_ARGUMENT`; the message names the field and never echoes the value): `user_id` and `item.product_id` are non-empty (not only whitespace) and at most 128 characters; `item` is present; `item.quantity` is 1 to 100 per `AddItem` call; the total of one product in a cart is at most 1000 (`AddItem` beyond that is `INVALID_ARGUMENT` and writes nothing). The `GetCart`/`EmptyCart` calls need a non-empty `user_id` too.

Errors from the storage layer carry a generic text, never the Redis exception (the detail is in the server log and on the span):

| Situation | gRPC status | Text |
|---|---|---|
| Redis unreachable or failing | `UNAVAILABLE` | `cart storage unavailable` |
| Redis did not answer within `REDIS_TIMEOUT_MS` | `DEADLINE_EXCEEDED` | `cart storage did not answer in time` |
| The caller cancelled or its own deadline passed | `CANCELLED` | `request cancelled` |
| Stored value cannot be parsed | `INTERNAL` | `internal error` |

(Before this change every Redis failure was `FAILED_PRECONDITION` with the exception text appended.)

### Storage model

- Redis key = `user_id` (raw, no prefix — the `IDistributedCache` instance has no `InstanceName`).
- Value = protobuf-serialised `Cart` (binary).
- **No TTL.** Carts live until Redis is wiped. `EmptyCart` writes an empty cart rather than deleting the key.
- `AddItem` is read-modify-write and not atomic: two concurrent adds for the same user can lose an update.

## Configuration

| Variable | Required | Default | Description |
|---|---|---|---|
| `REDIS_ADDR` | **Yes** | — | StackExchange.Redis configuration string, e.g. `cart-redis:6379`. Unset: the process prints one JSON error line (`startup failed`) and exits with code 1. Extra options go after commas: `cart-redis:6379,password=<secret>,ssl=false` |
| `REDIS_TIMEOUT_MS` | No | `2000` | Upper bound in milliseconds for the Redis work of ONE cart RPC (both round trips of `AddItem` together), 50 to 30000; the caller's own gRPC deadline still wins when it is shorter. Unset, empty, not a number or out of range means 2000 (a typo never removes the deadline). On expiry the RPC fails with `DEADLINE_EXCEEDED`. Covered by `RedisCartStoreFailureTests` |
| `ASPNETCORE_ENVIRONMENT` | No | `Production` (in container) | Standard ASP.NET Core |
| `Kestrel__Endpoints__Grpc__Url` | No | `http://0.0.0.0:7070` | Override the gRPC listener (standard .NET env-var-to-config mapping) |
| `LOG_LEVEL` | No | `info` | `debug`, `info`, `warn`/`warning` or `error` (case-insensitive; anything else means `info`). Framework (`Microsoft.*`) categories stay at `warning` or above |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | No | unset (telemetry off) | OTLP/**gRPC** endpoint for **traces, metrics and logs** (scheme required), e.g. `http://alloy.monitoring.svc.cluster.local:4317`. Telemetry is on only when this is an absolute http(s) URL; empty or malformed means off, never a crash (covered by `TelemetryConfigTests`). The legacy `COLLECTOR_SERVICE_ADDR` is removed. The `starting` log line says whether telemetry is on |
| `OTEL_SDK_DISABLED` | No | — | `true` (trimmed, case-insensitive) forces telemetry off even with an endpoint set (the legacy `ENABLE_TRACING` is removed and ignored) |
| `OTEL_METRIC_EXPORT_INTERVAL` | No | `15000` | Milliseconds between metric pushes |
| `OTEL_SERVICE_NAME` | No | `cart-service` | `service.name` (also the `service` field of every log line); keep the default, dashboards depend on it |
| `SERVICE_VERSION` | No | assembly version, else `dev` | `service.version` and the `version` log field. Chain: `SERVICE_VERSION`, then the assembly informational version (set at build with `-p:Version=...`; the csproj placeholder `0.0.0-dev` counts as unset), then `dev` |
| `SERVICE_COMMIT` | No | `unknown` | Commit id, logged in the `starting` line and the `commit` tag of `build_info`; set it at deploy time |
| `DEPLOYMENT_ENVIRONMENT` | No | `local` | `deployment.environment` resource attribute |
| `HOSTNAME` | No | machine name | `service.instance.id` (the pod name in Kubernetes) |
| `PYROSCOPE_ADDR` | No | — | Accepted by the platform contract but **not used**: .NET has no continuous profiler (accepted exception, OBS-40) |

Notes:

- Ports are defined in [`src/appsettings.json`](src/appsettings.json): Kestrel `Grpc` = `Http2` on 7070. The image also sets `ASPNETCORE_HTTP_PORTS=7070`, but explicit Kestrel endpoints take precedence. There is no plain `PORT` variable.
- There is **no profiling**: there is no continuous profiler for .NET, so `PYROSCOPE_ADDR` does nothing. This is a documented, accepted exception to OBS-40, not a gap to fix with a workaround.
- Redis is connected to lazily on first use, so the process starts even if Redis is down — it just reports `NOT_SERVING` and fails RPCs until Redis is reachable.
- Telemetry is **pushed**, not scraped: traces, metrics and logs share one OTLP/gRPC endpoint (Alloy). There is no `/metrics` endpoint and no scrape port. `service.instance.id` is the pod hostname, so replicas stay distinct in Mimir.
- Every OTLP export has a 2 s deadline and the host drains for at most 3 s on SIGTERM, so the process exits well within 10 s even when the collector accepts connections and never answers (measured, see [docs/observability.md](docs/observability.md)).

## Container image

Multi-stage [`Dockerfile`](Dockerfile):

| Stage | Base | What happens |
|---|---|---|
| builder | `mcr.microsoft.com/dotnet/sdk:10.0` | `dotnet restore` (csproj only, for layer caching), then `dotnet publish -c Release` |
| runtime | `mcr.microsoft.com/dotnet/aspnet:10.0` | copies publish output, runs as `app` |

- **Entrypoint:** `./cartservice` (working dir `/app`).
- **User:** the base image's `app`, renumbered to **UID/GID `10001`** with `groupmod`/`usermod` (numeric `USER 10001:10001`, verified on the built image). Build args `SERVICE_VERSION` (default `dev`) and `SERVICE_COMMIT` (default `unknown`) set the OCI labels `version`/`revision` (plus `source`, `licenses=Apache-2.0`) and the ENV of the same names.
- **ENV:** `ASPNETCORE_HTTP_PORTS=7070`, `DOTNET_EnableDiagnostics=0` (disables the diagnostics IPC socket, so no writable `/tmp` is needed for that).
- **EXPOSE:** `7070`. **`HEALTHCHECK`:** `bash -c 'exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_HTTP_PORTS:-7070}'` (TCP connect; no `nc`/`curl`/`wget` in the image and no `healthcheck` subcommand, so it does not check Redis). Kubernetes: use `grpc:` or `tcpSocket`.
- **Image size:** ~97 MB.
- **Build context:** a root-level `.dockerignore` (added) excludes `bin/`, `obj/`, `tests/`, `.git`. The previous `src/.dockerignore` had no effect, and a host-built `src/obj` made the in-image `dotnet publish` fail with `NETSDK1064`.

## Kubernetes deployment notes

**Workload:** stateless `Deployment`; scale freely. Needs a reachable Redis (the existing `cart-redis` chart / `redis:7-alpine`, port `6379`).

**Ports to declare**

| Name | Port | Protocol | Expose via Service? |
|---|---|---|---|
| `grpc` | `7070` | TCP (HTTP/2) | Yes — callers dial `cart-service:7070` |

**Service DNS name expected by callers:** `cart-service` (`frontend`: `CART_SERVICE_ADDR=cart-service:7070`, `checkout-service`: `CART_SERVICE_ADDR=cart-service:7070`). Use FQDNs if callers are in other namespaces.

**Probes** — the gRPC health check pings Redis, which is exactly what you want for *readiness* but **not** for liveness (restarting cart pods does not fix a Redis outage and would cause a restart storm):

```yaml
startupProbe:
  tcpSocket: { port: 7070 }
  periodSeconds: 2
  failureThreshold: 30
readinessProbe:            # Redis-aware: pod leaves the endpoints while Redis is down
  grpc: { port: 7070 }
  periodSeconds: 5
  timeoutSeconds: 6        # Redis ping can take up to ~5 s to time out
livenessProbe:             # process-only
  tcpSocket: { port: 7070 }
  periodSeconds: 10
  failureThreshold: 3
```

**Security context**

```yaml
securityContext:
  runAsNonRoot: true
  runAsUser: 10001
  runAsGroup: 10001
  allowPrivilegeEscalation: false
  capabilities: { drop: ["ALL"] }
  readOnlyRootFilesystem: true   # verified in-cluster; no writable /tmp was needed
```

**Resources** — requests sized from measured idle usage in the test stack (see the chart's `values.yaml` for limits):

```yaml
resources:
  requests: { cpu: 100m, memory: 64Mi }
  limits:   { cpu: "1", memory: 256Mi }
```

**Config / secrets:** `REDIS_ADDR` is not secret while Redis has no password. If you add auth, put the *whole* `REDIS_ADDR` string (it embeds `password=…`) in a `Secret` and use `secretKeyRef`.

**Telemetry:** pushed, not scraped — OTLP/gRPC to Alloy (`global.monitoring.otlpEndpoint`); no profiler. Metrics carry `job="cart-service"` and `instance=<pod>`; no scrape annotations or `ServiceMonitor` are needed.

**Helm chart:** [`platform-demo-helm/cart-service`](https://github.com/platform-boutique/platform-demo-helm/tree/main/cart-service) (built on the shared `platform-common` library chart). It ships with HA defaults — 2 replicas,
HPA, PodDisruptionBudget, topology spread, NetworkPolicy, restricted pod security context, probes and the full env contract (see Configuration) —
and was verified under the `restricted` Pod Security Standard on the earlier local 3-node kind cluster (since retired). The platform now targets AWS EKS, where this chart has not been deployed or verified yet.

**Deployment and admission policies:** Argo CD deploys the chart from `platform-demo-helm` (`envs/<env>/values/cart-service.yaml` pins the image digest; promotion is a pull request). In the cluster Kyverno enforces, for this service: pods named `app.kubernetes.io/name: cart-service` that run under the ServiceAccount of the same name (Cilium derives the network identity from that label), an image from this repository pinned by digest and signed by our release pipeline, a read-only root filesystem, resource requests and a memory limit, readiness and liveness probes and no API token. The Helm CI evaluates the rendered chart against these policies before merge; see [`platform-demo-helm/policies`](https://github.com/platform-boutique/platform-demo-helm/tree/main/policies).

## Observability

| Signal | Implementation | Destination |
|---|---|---|
| **Traces** | OpenTelemetry .NET: ASP.NET Core + HttpClient instrumentation, plus a manual client span per Redis operation (`redis AddItem`/`GetCart`/`EmptyCart`, source `Everly.Cart`, `db.system.name=redis`, no ids on the span, errors recorded as status + exception event; the StackExchange.Redis instrumentation package is not used: it needs the connection multiplexer, which the cache client creates internally, so using it means owning the connection first), OTLP/gRPC exporter (batch processor, bounded queue, 2 s export deadline) | Alloy `:4317` → Tempo |
| **Metrics** | OpenTelemetry .NET: ASP.NET Core, Kestrel, gRPC server and runtime instrumentation, `PeriodicExportingMetricReader` over OTLP/gRPC every 15 s, plus `build_info{service,version,commit}`. Pushed over OTLP; there is no scrape endpoint | Alloy → Mimir |
| **Logs** | One JSON object per line on stdout (below) **and** the OTel logging provider over OTLP/gRPC | Alloy → Loki |
| **Profiles** | none: .NET has no continuous profiler (accepted exception to OBS-40) | — |

**Log line shape** (same as the Go services): `timestamp` (RFC3339 UTC, microseconds), `severity` (`debug`, `info`, `warning`, `error`), `message`, `service`, `version`, then only when present `trace_id` and `span_id` (hex; taken from the current `Activity`, so they appear for lines written while a request is being handled and are absent otherwise), `request_id` (from an `x-request-id` gRPC header, none is sent today), `error` (`Type: message`, no stack trace) and the allow-listed `commit` and `telemetry` on the `starting` line. Anything else in the log state is dropped. An uncaught startup error is one JSON line on stdout (`startup failed`) and exit code 1.

```json
{"timestamp":"2026-11-14T00:13:05.270351Z","severity":"info","message":"starting","service":"cart-service","version":"9.9.9","commit":"abc1234","telemetry":"on (OTLP http://127.0.0.1:14317/)"}
```

Every signal carries `service.name`, `service.version`, `service.namespace=everly`, `deployment.environment` and `service.instance.id`. `build_info` is a gauge with the value 1.

Metric names the dashboard uses: `http_server_request_duration_seconds_{bucket,count,sum}` (labels `http_route` — e.g. `/hipstershop.CartService/AddItem` — `http_response_status_code`, `http_request_method`) and `http_server_active_requests`; plus `kestrel_*`, `aspnetcore_*` and `dotnet_*` runtime metrics. gRPC calls are HTTP/2 requests, so the per-method breakdown comes from `http_route`. The names differ from the Go services (`grpc_server_*`); dashboards use the Tempo span metrics for cross-language panels.

Every series carries `job="cart-service"` (from `service.name`) and `instance=<pod name>` (`service.instance.id`), which is what the Grafana dashboards select on. Logs land in Loki with `service_name="cart-service"`; the `trace_id` in the stdout line links to the trace in Tempo.

Logging what: the per-call lines log the cart owner's `userId` (an anonymous session id, not an email or name) and product ids; no card data, credentials or tokens pass through this service.

## Running locally

Requirements: .NET 10 SDK, Docker, a Redis, [`grpcurl`](https://github.com/fullstorydev/grpcurl).

```bash
# 1. Redis
docker run -d --name cart-redis -p 6379:6379 redis:7-alpine

# 2. run from source
cd src && REDIS_ADDR=localhost:6379 dotnet run

# or via Docker
docker build -t cart-service:local .
docker run --rm --network host -e REDIS_ADDR=localhost:6379 cart-service:local

# tests
dotnet test tests/cartservice.tests.csproj
```

NuGet packages are locked (`src/packages.lock.json`, `tests/packages.lock.json`, SEC-01). CI (`CI=true`) and the Dockerfile restore in locked mode, so a resolution that differs from the lock files fails the build. After changing a package version, run `dotnet restore cartservice.sln` locally (with `CI` unset) and commit the changed lock files.

### Manual gRPC testing (reflection is on)

```bash
grpcurl -plaintext -d '{"user_id":"u1","item":{"product_id":"OLJCESPC7Z","quantity":2}}' \
  localhost:7070 hipstershop.CartService/AddItem

grpcurl -plaintext -d '{"user_id":"u1"}' localhost:7070 hipstershop.CartService/GetCart
grpcurl -plaintext -d '{"user_id":"u1"}' localhost:7070 hipstershop.CartService/EmptyCart

grpcurl -plaintext localhost:7070 grpc.health.v1.Health/Check   # NOT_SERVING if Redis is down
```

## Develop

Requirements: .NET 10 SDK (`global.json` asks for 10.0.100 and rolls forward; the host has 10.0.115), Docker for the image and the Redis integration test, Python 3.12 for the docs check. The commands below are the ones the shared pipeline (`platform-demo-workflows`, `service-ci-dotnet`) runs, so a failure can be reproduced without pushing. `Running locally` above has the run commands.

| Step | Command | Result on 2026-11-21 |
|---|---|---|
| Format | `dotnet format cartservice.sln --verify-no-changes` | run, exit 0 |
| Build | `dotnet build cartservice.sln --configuration Release /warnaserror` | run, 0 errors |
| Unit tests | `dotnet test tests/cartservice.tests.csproj` (the Redis integration tests skip without `REDIS_ADDR`, the E2E tests skip without a reachable service) | run: 114 passed, 11 skipped |
| Integration tests (Redis) | `REDIS_ADDR=127.0.0.1:56379 dotnet test tests/cartservice.tests.csproj` against a throwaway `redis:7-alpine` (`docker run -d --rm --name <unique name> --memory 128m -p 127.0.0.1:56379:6379 redis:7-alpine`, remove it afterwards) | run: 120 passed, 5 skipped (the E2E tests); the container was removed afterwards |
| End-to-end tests | `GRPC_HOST=localhost:7070 dotnet test tests/cartservice.tests.csproj` against a running service | not run (they skipped: no service was reachable) |
| Vulnerable packages | `dotnet list src/cartservice.csproj package --vulnerable --include-transitive` | run: no vulnerable packages |
| Image | `docker build -t platform-demo/cart-service:local .` | not run today |
| Image scan | `trivy image --severity CRITICAL,HIGH --exit-code 1 platform-demo/cart-service:local` | not run today |
| Secret scan | `pre-commit run --all-files` (`.pre-commit-config.yaml`, gitleaks) or `gitleaks git --redact` | run with the gitleaks binary of the pre-commit cache (`gitleaks git --redact`, full history, 46 commits): no leaks found |
| Workflow lint | `actionlint` | run (actionlint 1.7.12), no findings |
| Docs | `mkdocs build --strict` (mkdocs 1.6.1 + mkdocs-techdocs-core 1.7.1, Python 3.12 venv) and the link check `python3 -I tools/check-md-links.py .` from `platform-demo-workflows` (relative links in `README.md` and `docs/`) | run, both passed |

## Project structure

```
├── src/
│   ├── Program.cs                 # DI, Redis cache, gRPC + reflection, JSON logs, OTel (traces/metrics/logs), endpoints
│   ├── JsonLog.cs, JsonLogFormatter.cs  # the one stdout log shape (OBS-30)
│   ├── ServiceIdentity.cs         # name, version chain, commit, environment, instance id
│   ├── TelemetryConfig.cs, TelemetryExtensions.cs  # OTLP on/off contract, resource, build_info, export deadlines
│   ├── RequestIdInterceptor.cs    # x-request-id -> logging scope
│   ├── appsettings.json           # Kestrel endpoints (7070 h2); log level comes from LOG_LEVEL
│   ├── cartservice.csproj         # net10.0, TreatWarningsAsErrors, package refs
│   ├── protos/cart.proto          # service contract
│   ├── services/
│   │   ├── CartService.cs         # gRPC handlers
│   │   └── HealthCheckService.cs  # grpc.health.v1 implementation (pings Redis)
│   └── store/
│       ├── ICartStore.cs
│       └── RedisCartStore.cs      # IDistributedCache-backed store
├── Dockerfile                     # sdk:10.0 builder -> aspnet:10.0 runtime
├── cartservice.sln
├── global.json / .editorconfig
└── tests/                         # unit tests, tests/Integration (Redis), tests/E2E (a running service, skipped when it is not reachable)
└── docs/                          # TechDocs pages (mkdocs.yml in the root), docs/adr/ = decisions
```

## Known limitations

- Carts never expire (no TTL) and `AddItem` is not atomic.
- Health is Redis-coupled; see probe guidance above.
- Logs are one JSON object per line on stdout through `JsonLogFormatter` (the one log shape of the standards); framework (`Microsoft.*`) categories stay at `warning` or above.
