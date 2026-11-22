# Container image

Multi-stage [`Dockerfile`](https://github.com/platform-boutique/app-cart-service/tree/main/Dockerfile):

| Stage | Base | What happens |
|---|---|---|
| builder | `mcr.microsoft.com/dotnet/sdk:10.0` | `dotnet restore` (csproj only, for layer caching), then `dotnet publish -c Release` |
| profiler | `mcr.microsoft.com/dotnet/sdk:10.0` | downloads the Grafana Pyroscope .NET profiler 1.6.1 (glibc x86_64) with `ADD --checksum`, keeps the two libraries and strips their debug info (build stage only) |
| runtime | `mcr.microsoft.com/dotnet/aspnet:10.0` | copies the publish output, the profiler libraries (`/opt/pyroscope`) and `container/entrypoint.sh`, runs as `app` |

- **Entrypoint:** `/app/entrypoint.sh` (exec form), which execs `./cartservice` (working dir `/app`). It decides before the .NET runtime starts whether the Pyroscope profiler is loaded: only when `PYROSCOPE_ADDR` is a valid http(s) URL (see [ADR 2](adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md)). With profiling off it changes nothing.
- **User:** the base image's `app`, renumbered to **UID/GID `10001`** with `groupmod`/`usermod` (numeric `USER 10001:10001`, verified on the built image). Build args `SERVICE_VERSION` (default `dev`) and `SERVICE_COMMIT` (default `unknown`) set the OCI labels `version`/`revision` (plus `source`, `licenses=Apache-2.0`) and the ENV of the same names.
- **ENV:** `ASPNETCORE_HTTP_PORTS=7070`, `DOTNET_EnableDiagnostics=0` (disables the diagnostics IPC socket, so no writable `/tmp` is needed for that; it also disables profilers, so the entrypoint sets `DOTNET_EnableDiagnostics=1` with `_IPC=0`, `_Debugger=0`, `_Profiler=1` for the service process when profiling is on). No `CORECLR_*` variable is set in the image: the profiler is loaded only through the entrypoint, so the healthcheck and `docker exec` shells never load it.
- **EXPOSE:** `7070`. **`HEALTHCHECK`** (exec form): `["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_HTTP_PORTS:-7070}"]` (TCP connect; no `nc`/`curl`/`wget` in the image and no `healthcheck` subcommand, so it does not check Redis). Kubernetes: use `grpc:` or `tcpSocket`.
- **Image size** (`docker image inspect`, 2026-11-22): 110.4 MB with the profiler (`docker images` content size 110 MB, disk usage 387 MB), 105.4 MB (disk 369 MB) for the same code without the profiler stage, so the profiler adds 4.9 MB by that measure and 18 MB by `docker images` disk usage (two stripped libraries of 12.8 MB and 35 KB, plus the entrypoint). The runtime base is `mcr.microsoft.com/dotnet/aspnet:10.0` pinned by digest (Ubuntu 24.04 based, glibc).
- **Build context:** a root-level `.dockerignore` (added) excludes `bin/`, `obj/`, `tests/`, `.git`. The previous `src/.dockerignore` had no effect, and a host-built `src/obj` made the in-image `dotnet publish` fail with `NETSDK1064`.
