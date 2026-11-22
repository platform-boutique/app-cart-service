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

# --- Stage 2: Run ---
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

ENV ASPNETCORE_HTTP_PORTS=7070 \
    DOTNET_EnableDiagnostics=0 \
    SERVICE_VERSION=${SERVICE_VERSION} \
    SERVICE_COMMIT=${SERVICE_COMMIT}

EXPOSE 7070

# Numeric so Kubernetes runAsNonRoot can verify it
USER 10001:10001

# The service has no healthcheck subcommand and the image has no nc/curl: TCP connect to the gRPC port with bash /dev/tcp
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/${ASPNETCORE_HTTP_PORTS:-7070}' || exit 1

ENTRYPOINT ["./cartservice"]