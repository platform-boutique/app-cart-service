# Configuration

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
| `PYROSCOPE_ADDR` | No | unset (profiling off) | Pyroscope server URL, e.g. `http://pyroscope:4040`. Continuous profiling with the Grafana Pyroscope .NET profiler is on only when this is an absolute http(s) URL with a host; empty or malformed means off, never a startup failure. `OTEL_SDK_DISABLED=true` and `ENABLE_PROFILING=0` force it off; `ENABLE_PROFILING=1` alone starts nothing. The image entrypoint decides before the runtime starts ([ADR 2](adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md)); the `starting` log line has a `profiling` field |
| `ENABLE_PROFILING` | No | — | `0` forces profiling off even with `PYROSCOPE_ADDR` set (same as the other services) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | `grpc` | Fixed: the exporters are configured in code for OTLP/gRPC (`OtlpExportProtocol.Grpc`), so another value has no effect |
| `OTEL_RESOURCE_ATTRIBUTES` | No | — | Not read by service code. The resource is the fixed set of OBS-02 (below); extra `key=value` pairs are only what the OpenTelemetry .NET SDK itself adds from this variable (not tested here) |
| `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG` | No | — | Not read by service code and no sampler is configured: the OpenTelemetry .NET SDK default applies (parent-based, always on; not tested here) |

Notes:

- Ports are defined in [`src/appsettings.json`](https://github.com/platform-boutique/app-cart-service/blob/main/src/appsettings.json): Kestrel `Grpc` = `Http2` on 7070. The image also sets `ASPNETCORE_HTTP_PORTS=7070`, but explicit Kestrel endpoints take precedence. There is no plain `PORT` variable.
- Profiling is a native CLR profiler (Grafana Pyroscope .NET profiler 1.6.1, installed in `/opt/pyroscope` of the image). It is loaded only when `container/entrypoint.sh` sees a valid `PYROSCOPE_ADDR`; a plain `dotnet run` never loads it. Application name `OTEL_SERVICE_NAME`, labels `service_name`, `version`, `environment`. CPU profiles only. See [Observability](observability.md).
- The profiler's own diagnostics go to a rotating log file in `/tmp` (`DD_TRACE_LOG_DIRECTORY`), not to stdout or stderr, so the service log stays one JSON object per line (`PYROSCOPE_LOG_STDERR=true` prints them on stderr when debugging). With a read-only root filesystem and no writable `/tmp` the profiler runs without a log file; the service and the profile upload are not affected (run on 2026-11-22, see [Observability](observability.md)).
- Redis is connected to lazily on first use, so the process starts even if Redis is down — it just reports `NOT_SERVING` and fails RPCs until Redis is reachable.
- Telemetry is **pushed**, not scraped: traces, metrics and logs share one OTLP/gRPC endpoint (Alloy). There is no `/metrics` endpoint and no scrape port. `service.instance.id` is the pod hostname, so replicas stay distinct in Mimir.
- Every OTLP export has a 2 s deadline and the host drains for at most 3 s on SIGTERM, so the process exits well within 10 s even when the collector accepts connections and never answers (measured, see [Observability](observability.md)).
