# 2. Profiling with the Pyroscope .NET profiler

Status: accepted. Owner decision of 2026-11-22 (Stefan Mladenovski). It replaces the earlier version of this record, which accepted "no continuous profiler" as an exception to OBS-40; the owner did not accept that exception.

## Context

The platform contract has continuous profiles for every service (OBS-40): profiling is on only when `PYROSCOPE_ADDR` is set, and the profiles reach Pyroscope under the service name with the tags `service_name`, `version` and `environment`. The Go, Node, Python and Java services do this with an in-process SDK or agent. .NET has no such SDK: the Grafana Pyroscope .NET profiler (`grafana/pyroscope-dotnet`) is a native CLR profiler. The runtime loads it at process start from `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER` and `CORECLR_PROFILER_PATH`, so the application cannot switch it on from its own code.

## Decision

cart-service profiles with the Pyroscope .NET profiler, switched on only through the same environment contract as the other services:

- **Switch.** `container/entrypoint.sh` is the image's exec-form `ENTRYPOINT`. Before it execs `./cartservice` it decides: profiling is on only when `PYROSCOPE_ADDR` is an absolute http(s) URL with a host; `OTEL_SDK_DISABLED=true` or `ENABLE_PROFILING=0` force it off; unset, empty or malformed means off. When on, it exports the `CORECLR_*`, `LD_PRELOAD` and `PYROSCOPE_*` variables for the service process only (the Dockerfile sets none of them, so the healthcheck and `docker exec` shells are not profiled). When off, nothing is exported and the service runs exactly as without the profiler. The script never fails: an unusable value or missing profiler files print one JSON `warning` line and the service starts without profiling.
- **Identity.** Application name = `OTEL_SERVICE_NAME` (default `cart-service`), from which the profiler sets `service_name` itself (a second `service_name` label makes Pyroscope reject every profile: seen and fixed on 2026-11-22); static labels `version` (`SERVICE_VERSION`, default `dev`) and `environment` (`DEPLOYMENT_ENVIRONMENT`, default `local`), so the tags are the same as for the other services. The `starting` log line carries a `profiling` field (`off`, `on (Pyroscope <url>)`, or a "requested but not loaded" message), computed by `ProfilingConfig`.
- **Diagnostics.** The image sets `DOTNET_EnableDiagnostics=0`, which also disables profilers on .NET 8 and later. When profiling is on, the entrypoint sets `DOTNET_EnableDiagnostics=1` with `_IPC=0` and `_Debugger=0` and `_Profiler=1`, so the diagnostics IPC socket and the debugger stay off.
- **Packaging.** A Dockerfile stage downloads `pyroscope.<version>-glibc-x86_64.tar.gz` of release `pyroscope-1.6.1` (published 2026-11-09, the newest release that was at least 7 days old on 2026-11-22), verified with `ADD --checksum=sha256:...` (the SHA-256 GitHub shows for the asset), keeps only `Pyroscope.Profiler.Native.so` and `Pyroscope.Linux.ApiWrapper.x64.so`, strips the 170 MB of debug info and installs them in `/opt/pyroscope` of the `aspnet:10.0` runtime image (Ubuntu 24.04 based at the pinned digest, glibc; verified 2026-11-22). The image stays amd64 only (IMG-04).
- **Profile types.** The agent's defaults: CPU. Allocation, lock, exception, heap and wall-time profiling stay off (they cost more and are not required by OBS-40).
- **Tools the entrypoint needs.** `/bin/sh`, `tr`, `sed`, `date` and `dirname`, all present in the `aspnet` image (checked 2026-11-22). A launcher without a shell would need its own native or .NET program that starts the runtime; not worth it for this service. The HEALTHCHECK does not use the shell: it is `cartservice healthcheck` (a TCP connect built into the binary).
- **Tests.** `ProfilingConfigTests` (the rule as a C# function) and `ProfilerEntrypointTests` (the real script run in a temp directory over the same env matrix, which also checks that script and function agree).

## Consequences

- cart-service has profiles in Pyroscope like every other service; `local/verify-signals.py` no longer treats it as an exception.
- A native profiler runs inside the process when `PYROSCOPE_ADDR` is set. With it off (the default) there is no overhead and no change in behaviour. What was measured with it on is in [Observability](../observability.md).
- The image is larger (the stripped profiler libraries); the delta is in [Container image](../container-image.md).
- The profiler version is pinned in the Dockerfile with its checksum. Dependabot does not track a GitHub release download, so it is bumped by hand (change `PYROSCOPE_DOTNET_VERSION` and `PYROSCOPE_DOTNET_SHA256` together; the build fails on a checksum mismatch).
- The profiler prints text lines to stderr and writes a rotating log file under `/var/log/pyroscope` by default. The entrypoint switches stderr off (`PYROSCOPE_LOG_STDERR=false`, so stdout/stderr keep one JSON object per line) and points the file to `/tmp` (`DD_TRACE_LOG_DIRECTORY`), because the default directory is not writable for the user 10001. With a read-only root filesystem and no writable `/tmp` the service starts and stays healthy without a profiler log file (run on 2026-11-22).
- No span-profile correlation (the .NET profiler has no OpenTelemetry link in this setup); OBS-40 only asks for it where the SDK supports it.
