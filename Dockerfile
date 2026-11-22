# --- Stage 1: Build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS builder

WORKDIR /app

COPY src/cartservice.csproj src/packages.lock.json ./src/
# Locked mode (SEC-01): fails when the resolved packages differ from packages.lock.json
RUN dotnet restore --locked-mode src/cartservice.csproj

COPY src/ ./src/
RUN dotnet publish src/cartservice.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# --- Stage 2: Grafana Pyroscope .NET profiler (OBS-40, ADR 2) ---
# Release pyroscope-1.6.1 (2026-11-09), glibc x86_64 (amd64 only, IMG-04). ADD --checksum makes the build fail when the
# download differs from this SHA-256 (the digest GitHub shows for the release asset). Newer release: change both ARGs.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS profiler
ARG TARGETARCH
ARG PYROSCOPE_DOTNET_VERSION=1.6.1
ARG PYROSCOPE_DOTNET_SHA256=9efda5cf621511ff937a0f765fe8af0ea59ab8c47a227c8afcf25637779dd36f
ADD --checksum=sha256:${PYROSCOPE_DOTNET_SHA256} \
    https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-${PYROSCOPE_DOTNET_VERSION}/pyroscope.${PYROSCOPE_DOTNET_VERSION}-glibc-x86_64.tar.gz \
    /tmp/pyroscope.tar.gz
# The release ships both libraries with 170 MB of debug info: strip it (the profiler only needs the dynamic symbols).
# binutils is installed in this build stage only; it is not part of the final image.
RUN test "${TARGETARCH:-amd64}" = "amd64" \
    && apt-get update \
    && apt-get install -y --no-install-recommends binutils \
    && mkdir /out \
    && tar -xzf /tmp/pyroscope.tar.gz -C /out Pyroscope.Profiler.Native.so Pyroscope.Linux.ApiWrapper.x64.so \
    && strip --strip-unneeded /out/*.so \
    && chmod 0644 /out/*.so

# --- Stage 3: Run ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586

ARG SERVICE_VERSION=dev
ARG SERVICE_COMMIT=unknown

LABEL org.opencontainers.image.title="cart-service" \
      org.opencontainers.image.source="https://github.com/platform-boutique/app-cart-service" \
      org.opencontainers.image.revision="${SERVICE_COMMIT}" \
      org.opencontainers.image.version="${SERVICE_VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0"

# The base image's non-root user "app" has UID 1654: renumber it to 10001:10001 (IMG-02)
RUN groupmod --gid 10001 app && usermod --uid 10001 --gid 10001 app

WORKDIR /app

COPY --from=builder /app/publish .
# The profiler is only loaded when container/entrypoint.sh decides so (PYROSCOPE_ADDR set): nothing below sets CORECLR_*
COPY --from=profiler /out/ /opt/pyroscope/
COPY --chmod=0755 container/entrypoint.sh /app/entrypoint.sh

ENV ASPNETCORE_HTTP_PORTS=7070 \
    DOTNET_EnableDiagnostics=0 \
    SERVICE_VERSION=${SERVICE_VERSION} \
    SERVICE_COMMIT=${SERVICE_COMMIT}

EXPOSE 7070

# Numeric so Kubernetes runAsNonRoot can verify it
USER 10001:10001

# `cartservice healthcheck` is a TCP connect to the gRPC port (CODE-03: no shell, nc or curl needed; it does not check Redis).
# It runs the binary directly, not through the entrypoint, so the profiler is never loaded into the probe.
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/app/cartservice", "healthcheck"]

# Decides before the runtime starts whether the Pyroscope profiler is loaded (PYROSCOPE_ADDR), then execs ./cartservice
ENTRYPOINT ["/app/entrypoint.sh"]
