using cartservice;
using cartservice.services;
using cartservice.store;

// `cartservice healthcheck` is the container HEALTHCHECK: a TCP connect to the gRPC port, no host, no telemetry.
if (args is [HealthProbe.Command])
{
    return HealthProbe.Run(HealthProbe.Port(Environment.GetEnvironmentVariable), HealthProbe.Timeout);
}

// Uncaught startup errors (missing REDIS_ADDR, port in use, ...) are one JSON line on stdout, exit code 1.
ServiceIdentity identity = ServiceIdentity.FromProcess();
try
{
    return Run(args, identity);
}
#pragma warning disable CA1031 // top-level catch-all: every startup failure becomes the same JSON line
catch (Exception ex)
#pragma warning restore CA1031
{
    JsonLog.WriteStartupError(ex, identity, Console.Out);
    return 1;
}

static int Run(string[] args, ServiceIdentity identity)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // --- Logs: one JSON object per line on stdout, level from LOG_LEVEL (see JsonLog) ---
    LogLevel logLevel = LogSettings.Level(Environment.GetEnvironmentVariable);
    builder.Logging.ClearProviders();
    _ = builder.Logging.AddConsoleFormatter<JsonLogFormatter, JsonLogFormatterOptions>(o => o.Identity = identity);
    _ = builder.Logging.AddConsole(o => o.FormatterName = JsonLogFormatter.FormatterName);
    _ = builder.Logging.SetMinimumLevel(logLevel);
    _ = builder.Logging.AddFilter("Microsoft", logLevel < LogLevel.Warning ? LogLevel.Warning : logLevel);
    _ = builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", logLevel);

    // SIGTERM: stop accepting, drain for at most this long, then flush telemetry (each export is bounded too): total < 10 s.
    _ = builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TelemetryExtensions.ShutdownTimeout);

    // --- Redis ---
    string redisAddr = Environment.GetEnvironmentVariable("REDIS_ADDR")
        ?? throw new InvalidOperationException("REDIS_ADDR environment variable is not set.");

    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisAddr;
    });

    // Register both the concrete type (for Redis health-check) and the abstraction (for CartService).
    // Deadline for the Redis work of one RPC (REDIS_TIMEOUT_MS, default 2000 ms).
    builder.Services.AddSingleton(CartStoreOptions.FromEnvironment(Environment.GetEnvironmentVariable));
    builder.Services.AddSingleton<RedisCartStore>();
    builder.Services.AddSingleton<ICartStore>(sp => sp.GetRequiredService<RedisCartStore>());

    // --- gRPC ---
    builder.Services.AddGrpc(o => o.Interceptors.Add<RequestIdInterceptor>());
    builder.Services.AddGrpcReflection();

    // --- Traces, metrics and logs via OpenTelemetry, pushed over OTLP/gRPC (Alloy -> Tempo/Mimir/Loki) ---
    // Only when OTEL_EXPORTER_OTLP_ENDPOINT is an absolute http(s) URL (e.g. http://alloy:4317); OTEL_SDK_DISABLED=true
    // forces it off (see TelemetryConfig). Unset, empty or malformed means off: nothing is registered.
    Uri? otlpEndpoint = TelemetryConfig.OtlpEndpoint(Environment.GetEnvironmentVariable);
    if (otlpEndpoint is not null)
    {
        int metricIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("OTEL_METRIC_EXPORT_INTERVAL"), out int ms) ? ms : 15000;
        _ = builder.Services.AddEverlyTelemetry(identity, otlpEndpoint, metricIntervalMs);
    }

    // --- App ---
    WebApplication app = builder.Build();

    app.UseRouting();
    app.MapGrpcService<CartService>();
    app.MapGrpcService<HealthCheckService>();
    app.MapGrpcReflectionService();
    _ = app.MapGet("/", () => "cart-service gRPC — use a gRPC client to interact.");

    // The "starting" line: version and commit (CODE-06) and whether telemetry and profiling are on.
    app.Logger.Log(
        LogLevel.Information,
        default,
        new KeyValuePair<string, object?>[]
        {
            new("commit", identity.Commit),
            new("telemetry", otlpEndpoint is null ? "off" : $"on (OTLP {otlpEndpoint})"),
            new("profiling", ProfilingConfig.Describe(Environment.GetEnvironmentVariable)),
        },
        null,
        static (_, _) => "starting");

    app.Run();
    return 0;
}
