# Configuration

| Variable | Required | Default | Description |
|---|---|---|---|
| `REDIS_ADDR` | **Yes** | — | StackExchange.Redis configuration string, e.g. `cart-redis:6379`. Unset: the process prints one JSON error line (`startup failed`) and exits with code 1. Extra options go after commas: `cart-redis:6379,password=<secret>,ssl=false` |
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

- Ports are defined in [`src/appsettings.json`](https://github.com/platform-boutique/app-cart-service/blob/main/src/appsettings.json): Kestrel `Grpc` = `Http2` on 7070. The image also sets `ASPNETCORE_HTTP_PORTS=7070`, but explicit Kestrel endpoints take precedence. There is no plain `PORT` variable.
- There is **no profiling**: there is no continuous profiler for .NET, so `PYROSCOPE_ADDR` does nothing. This is a documented, accepted exception to OBS-40, not a gap to fix with a workaround.
- Redis is connected to lazily on first use, so the process starts even if Redis is down — it just reports `NOT_SERVING` and fails RPCs until Redis is reachable.
- Telemetry is **pushed**, not scraped: traces, metrics and logs share one OTLP/gRPC endpoint (Alloy). There is no `/metrics` endpoint and no scrape port. `service.instance.id` is the pod hostname, so replicas stay distinct in Mimir.
- Every OTLP export has a 2 s deadline and the host drains for at most 3 s on SIGTERM, so the process exits well within 10 s even when the collector accepts connections and never answers (measured, see [Observability](observability.md)).
