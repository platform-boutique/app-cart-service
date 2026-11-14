using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace cartservice
{
    public static class TelemetryExtensions
    {
        /// <summary>Per-export deadline. Alloy answers in milliseconds; this bounds every flush against a collector that never does.</summary>
        public const int ExportTimeoutMs = 2000;

        /// <summary>Host shutdown budget (drain in-flight calls); the exporter flushes at disposal add at most 3 x <see cref="ExportTimeoutMs"/>.</summary>
        public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

        public const string BuildInfoMeter = "Everly.BuildInfo";

        /// <summary>The resource attributes on every signal (OBS-02).</summary>
        public static ResourceBuilder AddIdentity(this ResourceBuilder builder, ServiceIdentity id)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(id);
            return builder
                .AddService(id.Name, serviceNamespace: ServiceIdentity.Namespace, serviceVersion: id.Version, serviceInstanceId: id.InstanceId)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", id.Environment)]);
        }

        /// <summary>
        /// Traces, metrics and logs over OTLP/gRPC to <paramref name="endpoint"/>. Everything is asynchronous (batch processors with
        /// bounded queues, periodic metric reader) and every export has a <see cref="ExportTimeoutMs"/> deadline.
        /// Also registers the <c>build_info{service,version,commit}</c> gauge.
        /// </summary>
        public static IServiceCollection AddEverlyTelemetry(this IServiceCollection services, ServiceIdentity id, Uri endpoint, int metricIntervalMs)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(endpoint);

            void ConfigureOtlp(OtlpExporterOptions o)
            {
                o.Endpoint = endpoint;
                o.Protocol = OtlpExportProtocol.Grpc;
                o.TimeoutMilliseconds = ExportTimeoutMs;
            }

            KeyValuePair<string, object?>[] buildTags =
            [
                new("service", id.Name),
                new("version", id.Version),
                new("commit", id.Commit),
            ];
            Meter buildInfo = new(BuildInfoMeter);
            _ = buildInfo.CreateObservableGauge("build_info", () => new Measurement<int>(1, buildTags), description: "Build information of the running service (value is always 1)");

            _ = services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddIdentity(id))
                .WithTracing(tracing =>
                {
                    _ = tracing
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddOtlpExporter(ConfigureOtlp);
                })
                .WithMetrics(metrics =>
                {
                    _ = metrics
                        .AddAspNetCoreInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter("Grpc.AspNetCore.Server") // built-in gRPC server call metrics
                        .AddMeter(BuildInfoMeter)
                        .AddOtlpExporter((o, reader) =>
                        {
                            ConfigureOtlp(o);
                            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = metricIntervalMs;
                        });
                })
                .WithLogging(logging =>
                {
                    _ = logging.AddOtlpExporter(ConfigureOtlp);
                });

            return services;
        }
    }
}
