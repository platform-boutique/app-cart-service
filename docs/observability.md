# Observability

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

## Shutdown and the collector (CODE-04, OBS-04)

Startup never waits for the collector and the exporters are asynchronous. Every OTLP export has a 2 s deadline and `HostOptions.ShutdownTimeout` is 3 s, so SIGTERM stops accepting, drains, flushes and exits within 10 s even when the collector accepts connections and never answers.

What was run (2026-11-14, .NET 10 SDK container, `--network host`):

- `TelemetryTests.BlackholedCollector_NeverBlocksStartupOrShutdown`: a TCP listener that accepts and never replies; startup under 5 s, host stop plus dispose under 9 s (monotonic clock). Passes.
- The published app against the same kind of socket with `OTEL_METRIC_EXPORT_INTERVAL=1000`: SIGTERM to exit code 0 in 4.35 s.
- `REDIS_ADDR` unset: one JSON `startup failed` line, exit code 1.
- `LOG_LEVEL=warn`: the `info` lines (including `starting`) are not written.

Not run: the stack in `local/` (the compose stack was stopped on purpose), so the Loki and Tempo side of the trace links is not verified here.

## Profiling (OBS-40)

Grafana Pyroscope .NET profiler 1.6.1 (glibc x86_64 build, SHA-256 checked at image build), a native CLR profiler loaded by the image entrypoint only when `PYROSCOPE_ADDR` is a valid http(s) URL; the decision table, the environment it exports and the reasons are in [ADR 2](adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md). Without `PYROSCOPE_ADDR` (or with `OTEL_SDK_DISABLED=true` / `ENABLE_PROFILING=0`) nothing is loaded and the service behaves as before. The `starting` log line says which: `"profiling":"off"`, `"on (Pyroscope http://pyroscope:4040/)"`, or a "requested but not loaded" message when the runtime did not get the profiler.

What was run (2026-11-22, image built from this repository, local compose stack with `OBSERVABILITY=1`):

- Unit tests: `ProfilingConfigTests` (the rule) and `ProfilerEntrypointTests` (the real `container/entrypoint.sh` run in a temp directory over a 19-row env matrix, forwarded arguments, exit code, missing profiler files, malformed address; every row also checked against `ProfilingConfig`).
- The image on a throwaway Redis, `PYROSCOPE_ADDR` set to an address nobody listens on: healthy, runs as `10001:10001`, `/proc/1/maps` shows the profiler libraries loaded, stdout is only JSON lines, stop gives exit code 0. Without `PYROSCOPE_ADDR`: no profiler library mapped, `DOTNET_EnableDiagnostics=0` unchanged. With `--read-only` and no `/tmp`, and with `--read-only --tmpfs /tmp` (the profiler's log file then appears in `/tmp`): healthy in both.
- In the whole shop: Pyroscope had `service_name=cart-service` with the labels `version` and `environment`, CPU profile types (`process_cpu`) and a flame graph of the cart service with frames such as `cartservice.services!CartService.<GetCart>d__4.MoveNext` and `cartservice.store!RedisCartStore.<RunAsync>d__14<System.__Canon>.MoveNext`; `local/verify-signals.py` shows profiles for cart-service like for the other 13 services; the container stopped with exit code 0 in 1.7 s (`docker stop`).
- Bug found on the way: the first version also passed `service_name` as a label; the profiler already derives it from the application name and Pyroscope rejected every profile (`duplicate label name: service_name`). Fixed, with a test that the label is not set.

Not measured: the CPU and latency overhead of the profiler under load (one `docker stats` sample of the cart container after the smoke tests: 79 MiB, about 9 % CPU of one core at that moment, against 52 MiB without any telemetry and 82 MiB with OTLP according to the local stack notes), allocation, lock, exception and heap profiles (off), Windows and arm64.
