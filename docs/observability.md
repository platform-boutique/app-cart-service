# Observability

| Signal | Implementation | Destination |
|---|---|---|
| **Traces** | OpenTelemetry .NET: ASP.NET Core + HttpClient instrumentation, OTLP/gRPC exporter (batch processor, bounded queue, 2 s export deadline) | Alloy `:4317` → Tempo |
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

## Shutdown and the collector (CODE-04, OBS-04)

Startup never waits for the collector and the exporters are asynchronous. Every OTLP export has a 2 s deadline and `HostOptions.ShutdownTimeout` is 3 s, so SIGTERM stops accepting, drains, flushes and exits within 10 s even when the collector accepts connections and never answers.

What was run (2026-11-14, .NET 10 SDK container, `--network host`):

- `TelemetryTests.BlackholedCollector_NeverBlocksStartupOrShutdown`: a TCP listener that accepts and never replies; startup under 5 s, host stop plus dispose under 9 s (monotonic clock). Passes.
- The published app against the same kind of socket with `OTEL_METRIC_EXPORT_INTERVAL=1000`: SIGTERM to exit code 0 in 4.35 s.
- `REDIS_ADDR` unset: one JSON `startup failed` line, exit code 1.
- `LOG_LEVEL=warn`: the `info` lines (including `starting`) are not written.

Not run: the stack in `local/` (the compose stack was stopped on purpose), so the Loki and Tempo side of the trace links is not verified here.
