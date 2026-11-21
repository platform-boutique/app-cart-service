# Container image

Multi-stage [`Dockerfile`](https://github.com/platform-boutique/app-cart-service/tree/main/Dockerfile):

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
