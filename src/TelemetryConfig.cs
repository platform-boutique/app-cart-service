namespace cartservice
{
    /// <summary>
    /// Telemetry contract (same as the Go services): traces, metrics and logs are exported over OTLP/gRPC only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT (or the legacy COLLECTOR_SERVICE_ADDR, host:port) is set and non-empty;
    /// OTEL_SDK_DISABLED=true (or the legacy ENABLE_TRACING=0) forces it off. There is no localhost default.
    /// </summary>
    public static class TelemetryConfig
    {
        /// <summary>The OTLP/gRPC endpoint, or <c>null</c> when telemetry is off.</summary>
        public static Uri? OtlpEndpoint(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);

            string Value(string name) => (getEnv(name) ?? string.Empty).Trim();

            if (string.Equals(Value("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)
                || Value("ENABLE_TRACING") == "0")
            {
                return null;
            }

            string endpoint = Value("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (endpoint.Length > 0)
            {
                return new Uri(endpoint);
            }

            string legacy = Value("COLLECTOR_SERVICE_ADDR");
            return legacy.Length > 0 ? new Uri($"http://{legacy}") : null;
        }
    }
}
