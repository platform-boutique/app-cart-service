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
| `PYROSCOPE_ADDR` | No | unset (profiling off) | Pyroscope server URL, e.g. `http://pyroscope:4040`. Continuous profiling with the Grafana Pyroscope .NET profiler is on only when this is an absolute http(s) URL with a host; empty or malformed means off, never a startup failure. `OTEL_SDK_DISABLED=true` and `ENABLE_PROFILING=0` force it off; `ENABLE_PROFILING=1` alone starts nothing. The image entrypoint decides before the runtime starts ([ADR 2](docs/adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md)); the `starting` log line has a `profiling` field |
| `ENABLE_PROFILING` | No | — | `0` forces profiling off even with `PYROSCOPE_ADDR` set (same as the other services) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | `grpc` | Fixed: the exporters are configured in code for OTLP/gRPC (`OtlpExportProtocol.Grpc`), so another value has no effect |
| `OTEL_RESOURCE_ATTRIBUTES` | No | — | Not read by service code. The resource is the fixed set of OBS-02 (below); extra `key=value` pairs are only what the OpenTelemetry .NET SDK itself adds from this variable (not tested here) |
| `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG` | No | — | Not read by service code and no sampler is configured: the OpenTelemetry .NET SDK default applies (parent-based, always on; not tested here) |

Notes:

- Ports are defined in [`src/appsettings.json`](src/appsettings.json): Kestrel `Grpc` = `Http2` on 7070. The image also sets `ASPNETCORE_HTTP_PORTS=7070`, but explicit Kestrel endpoints take precedence. There is no plain `PORT` variable.
- Profiling is a native CLR profiler (Grafana Pyroscope .NET profiler 1.6.1, installed in `/opt/pyroscope` of the image). It is loaded only when `container/entrypoint.sh` sees a valid `PYROSCOPE_ADDR`; a plain `dotnet run` never loads it. Application name `OTEL_SERVICE_NAME`, labels `service_name`, `version`, `environment`. CPU profiles only. See [Observability](docs/observability.md).
- The profiler's own diagnostics go to a rotating log file in `/tmp` (`DD_TRACE_LOG_DIRECTORY`), not to stdout or stderr, so the service log stays one JSON object per line (`PYROSCOPE_LOG_STDERR=true` prints them on stderr when debugging). With a read-only root filesystem and no writable `/tmp` the profiler runs without a log file; the service and the profile upload are not affected (run on 2026-11-22, see [Observability](docs/observability.md)).
- Redis is connected to lazily on first use, so the process starts even if Redis is down — it just reports `NOT_SERVING` and fails RPCs until Redis is reachable.
- Telemetry is **pushed**, not scraped: traces, metrics and logs share one OTLP/gRPC endpoint (Alloy). There is no `/metrics` endpoint and no scrape port. `service.instance.id` is the pod hostname, so replicas stay distinct in Mimir.
- Every OTLP export has a 2 s deadline and the host drains for at most 3 s on SIGTERM, so the process exits well within 10 s even when the collector accepts connections and never answers (measured, see [docs/observability.md](docs/observability.md)).

## Container image

Multi-stage [`Dockerfile`](Dockerfile):

| Stage | Base | What happens |
|---|---|---|
| builder | `mcr.microsoft.com/dotnet/sdk:10.0` | `dotnet restore` (csproj only, for layer caching), then `dotnet publish -c Release` |
| profiler | `mcr.microsoft.com/dotnet/sdk:10.0` | downloads the Grafana Pyroscope .NET profiler 1.6.1 (glibc x86_64) with `ADD --checksum`, keeps the two libraries and strips their debug info (build stage only) |
| runtime | `mcr.microsoft.com/dotnet/aspnet:10.0` | copies the publish output, the profiler libraries (`/opt/pyroscope`) and `container/entrypoint.sh`, runs as `app` |

- **Entrypoint:** `/app/entrypoint.sh` (exec form), which execs `./cartservice` (working dir `/app`). It decides before the .NET runtime starts whether the Pyroscope profiler is loaded: only when `PYROSCOPE_ADDR` is a valid http(s) URL (see [ADR 2](docs/adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md)). With profiling off it changes nothing.
- **User:** the base image's `app`, renumbered to **UID/GID `10001`** with `groupmod`/`usermod` (numeric `USER 10001:10001`, verified on the built image). Build args `SERVICE_VERSION` (default `dev`) and `SERVICE_COMMIT` (default `unknown`) set the OCI labels `version`/`revision` (plus `source`, `licenses=Apache-2.0`) and the ENV of the same names.
- **ENV:** `ASPNETCORE_HTTP_PORTS=7070`, `DOTNET_EnableDiagnostics=0` (disables the diagnostics IPC socket, so no writable `/tmp` is needed for that; it also disables profilers, so the entrypoint sets `DOTNET_EnableDiagnostics=1` with `_IPC=0`, `_Debugger=0`, `_Profiler=1` for the service process when profiling is on). No `CORECLR_*` variable is set in the image: the profiler is loaded only through the entrypoint, so the healthcheck and `docker exec` shells never load it.
- **EXPOSE:** `7070`. **`HEALTHCHECK`** (exec form): `["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_HTTP_PORTS:-7070}"]` (TCP connect; no `nc`/`curl`/`wget` in the image and no `healthcheck` subcommand, so it does not check Redis). Kubernetes: use `grpc:` or `tcpSocket`.
- **Image size** (`docker image inspect`, 2026-11-22): 110.4 MB with the profiler (`docker images` content size 110 MB, disk usage 387 MB), 105.4 MB (disk 369 MB) for the same code without the profiler stage, so the profiler adds 4.9 MB by that measure and 18 MB by `docker images` disk usage (two stripped libraries of 12.8 MB and 35 KB, plus the entrypoint). The runtime base is `mcr.microsoft.com/dotnet/aspnet:10.0` pinned by digest (Ubuntu 24.04 based, glibc).
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

**Telemetry:** pushed, not scraped — OTLP/gRPC to Alloy (`global.monitoring.otlpEndpoint`); profiles are pushed to Pyroscope (`PYROSCOPE_ADDR`, off when unset; the Pyroscope .NET profiler loaded by the image entrypoint). Metrics carry `job="cart-service"` and `instance=<pod>`; no scrape annotations or `ServiceMonitor` are needed.

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
| **Profiles** | Continuous CPU profiles with the Grafana Pyroscope .NET profiler (native CLR profiler, release 1.6.1), pushed directly to Pyroscope; on only with `PYROSCOPE_ADDR` (off when unset, `OTEL_SDK_DISABLED=true` and `ENABLE_PROFILING=0` force it off). Application name = `OTEL_SERVICE_NAME`, so `service_name=cart-service`, plus the labels `version` and `environment`; no span-profile link | Pyroscope `:4040` |

**Log line shape** (same as the Go services): `timestamp` (RFC3339 UTC, microseconds), `severity` (`debug`, `info`, `warning`, `error`), `message`, `service`, `version`, then only when present `trace_id` and `span_id` (hex; taken from the current `Activity`, so they appear for lines written while a request is being handled and are absent otherwise), `request_id` (from an `x-request-id` gRPC header, none is sent today), `error` (`Type: message`, no stack trace) and the allow-listed `commit`, `telemetry` and `profiling` on the `starting` line. Anything else in the log state is dropped. An uncaught startup error is one JSON line on stdout (`startup failed`) and exit code 1.

```json
{"timestamp":"2026-11-14T00:13:05.270351Z","severity":"info","message":"starting","service":"cart-service","version":"9.9.9","commit":"abc1234","telemetry":"on (OTLP http://127.0.0.1:14317/)","profiling":"off"}
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

| Step | Command | Result on 2026-11-22 |
|---|---|---|
| Format | `dotnet format cartservice.sln --verify-no-changes` | run in `mcr.microsoft.com/dotnet/sdk:10.0`, exit 0 |
| Build | `CI=true dotnet build cartservice.sln --configuration Release /warnaserror` (locked restore) | run in the SDK container: 0 warnings, 0 errors |
| Unit tests | `dotnet test tests/cartservice.tests.csproj` (the Redis integration tests skip without `REDIS_ADDR`, the E2E tests skip without a reachable service) | run on the host SDK (10.0.115): 159 passed, 11 skipped |
| Integration tests (Redis) | `REDIS_ADDR=127.0.0.1:56391 dotnet test tests/cartservice.tests.csproj` against a throwaway `redis:7-alpine` (`docker run -d --rm --name <unique name> --memory 128m -p 127.0.0.1:56391:6379 redis:7-alpine`, remove it afterwards) | run in the SDK container: 165 passed, 5 skipped (the E2E tests); the container was removed afterwards |
| End-to-end tests | `GRPC_HOST=127.0.0.1:57071 dotnet test tests/cartservice.tests.csproj --filter FullyQualifiedName~E2E` against the built image (throwaway Redis + the image, profiling on, both removed afterwards) | run: 5 passed |
| Vulnerable packages | `dotnet list src/cartservice.csproj package --vulnerable --include-transitive` | run: no vulnerable packages |
| Image | `docker build --platform linux/amd64 --build-arg SERVICE_VERSION=final-test --build-arg SERVICE_COMMIT=<sha> -t platform-demo/cart-service:local .` | run: built (110 MB content, 387 MB disk), user `10001:10001`, exec-form healthcheck healthy, starts and exits 0 on stop with and without `PYROSCOPE_ADDR` |
| Image scan | `trivy image --severity CRITICAL,HIGH --exit-code 1 platform-demo/cart-service:local` (aquasec/trivy 0.76.1) | run: 0 vulnerabilities, exit 0 |
| Whole shop | `SKIP_BUILD=1 OBSERVABILITY=1 local/up.sh`, `local/smoke.sh` three times, `local/verify-signals.py` | run: smoke passed 3 times (87 PASS each); verify-signals shows traces, logs, log+trace_id, metrics and profiles for cart-service (the only open cell of the matrix is review-service log+trace_id, which needs a posted review); see [Observability](observability.md) |
| Secret scan | `pre-commit run --all-files` (`.pre-commit-config.yaml`, gitleaks) or `gitleaks git --redact` | run with the gitleaks binary of the pre-commit cache (`gitleaks git --redact`, full history of the branch): no leaks found |
| Workflow lint | `actionlint` | run (actionlint from `~/.local/bin`), no findings |
| Docs | `mkdocs build --strict` (mkdocs 1.6.1 + mkdocs-techdocs-core 1.7.1, Python 3.12 venv) and the link check `python3 -I tools/check-md-links.py .` from `platform-demo-workflows` (relative links in `README.md` and `docs/`) | run, both passed (31 links, 0 broken) |

## Project structure

```
├── src/
│   ├── Program.cs                 # DI, Redis cache, gRPC + reflection, JSON logs, OTel (traces/metrics/logs), endpoints
│   ├── JsonLog.cs, JsonLogFormatter.cs  # the one stdout log shape (OBS-30)
│   ├── ServiceIdentity.cs         # name, version chain, commit, environment, instance id
│   ├── TelemetryConfig.cs, TelemetryExtensions.cs  # OTLP on/off contract, resource, build_info, export deadlines
│   ├── ProfilingConfig.cs         # PYROSCOPE_ADDR rule as a function (the "starting" line); the entrypoint applies the same rule
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
├── container/entrypoint.sh        # image entrypoint: loads the Pyroscope .NET profiler only when PYROSCOPE_ADDR is valid
├── Dockerfile                     # sdk:10.0 builder + profiler stage -> aspnet:10.0 runtime
├── cartservice.sln
├── global.json / .editorconfig
└── tests/                         # unit tests, tests/Integration (Redis), tests/E2E (a running service, skipped when it is not reachable)
└── docs/                          # TechDocs pages (mkdocs.yml in the root), docs/adr/ = decisions
```

## Known limitations

- Carts never expire (no TTL) and `AddItem` is not atomic.
- Health is Redis-coupled; see probe guidance above.
- Logs are one JSON object per line on stdout through `JsonLogFormatter` (the one log shape of the standards); framework (`Microsoft.*`) categories stay at `warning` or above.
