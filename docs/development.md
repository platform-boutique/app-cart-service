# Develop & test

## Checks and commands

Requirements: .NET 10 SDK (`global.json` asks for 10.0.100 and rolls forward; the host has 10.0.115), Docker for the image and the Redis integration test, Python 3.12 for the docs check. The commands below are the ones the shared pipeline (`platform-demo-workflows`, `service-ci-dotnet`) runs, so a failure can be reproduced without pushing. `Running locally` above has the run commands.

| Step | Command | Result on 2026-11-21 |
|---|---|---|
| Format | `dotnet format cartservice.sln --verify-no-changes` | run, exit 0 |
| Build | `dotnet build cartservice.sln --configuration Release /warnaserror` | run, 0 errors |
| Unit tests | `dotnet test tests/cartservice.tests.csproj` (the Redis integration tests skip without `REDIS_ADDR`, the E2E tests skip without a reachable service) | run: 114 passed, 11 skipped |
| Integration tests (Redis) | `REDIS_ADDR=127.0.0.1:56379 dotnet test tests/cartservice.tests.csproj` against a throwaway `redis:7-alpine` (`docker run -d --rm --name <unique name> --memory 128m -p 127.0.0.1:56379:6379 redis:7-alpine`, remove it afterwards) | run: 120 passed, 5 skipped (the E2E tests); the container was removed afterwards |
| End-to-end tests | `GRPC_HOST=localhost:7070 dotnet test tests/cartservice.tests.csproj` against a running service | not run (they skipped: no service was reachable) |
| Vulnerable packages | `dotnet list src/cartservice.csproj package --vulnerable --include-transitive` | run: no vulnerable packages |
| Image | `docker build -t platform-demo/cart-service:local .` | not run today |
| Image scan | `trivy image --severity CRITICAL,HIGH --exit-code 1 platform-demo/cart-service:local` | not run today |
| Secret scan | `pre-commit run --all-files` (`.pre-commit-config.yaml`, gitleaks) or `gitleaks git --redact` | run with the gitleaks binary of the pre-commit cache (`gitleaks git --redact`, full history, 46 commits): no leaks found |
| Workflow lint | `actionlint` | run (actionlint 1.7.12), no findings |
| Docs | `mkdocs build --strict` (mkdocs 1.6.1 + mkdocs-techdocs-core 1.7.1, Python 3.12 venv) and the link check `python3 -I tools/check-md-links.py .` from `platform-demo-workflows` (relative links in `README.md` and `docs/`) | run, both passed |

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

### Manual gRPC testing (reflection is on)

```bash
grpcurl -plaintext -d '{"user_id":"u1","item":{"product_id":"OLJCESPC7Z","quantity":2}}' \
  localhost:7070 hipstershop.CartService/AddItem

grpcurl -plaintext -d '{"user_id":"u1"}' localhost:7070 hipstershop.CartService/GetCart
grpcurl -plaintext -d '{"user_id":"u1"}' localhost:7070 hipstershop.CartService/EmptyCart

grpcurl -plaintext localhost:7070 grpc.health.v1.Health/Check   # NOT_SERVING if Redis is down
```

## Project structure

```
├── src/
│   ├── Program.cs                 # DI, Redis cache, gRPC + reflection, JSON logs, OTel (traces/metrics/logs), endpoints
│   ├── JsonLog.cs, JsonLogFormatter.cs  # the one stdout log shape (OBS-30)
│   ├── ServiceIdentity.cs         # name, version chain, commit, environment, instance id
│   ├── TelemetryConfig.cs, TelemetryExtensions.cs  # OTLP on/off contract, resource, build_info, export deadlines
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
├── Dockerfile                     # sdk:10.0 builder -> aspnet:10.0 runtime
├── cartservice.sln
├── global.json / .editorconfig
└── tests/                         # unit tests, tests/Integration (Redis), tests/E2E (a running service, skipped when it is not reachable)
└── docs/                          # TechDocs pages (mkdocs.yml in the root), docs/adr/ = decisions
```
