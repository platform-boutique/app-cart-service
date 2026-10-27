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
                return HttpUri(endpoint);
            }

            string legacy = Value("COLLECTOR_SERVICE_ADDR");
            return legacy.Length > 0 ? HttpUri($"http://{legacy}") : null;
        }

        // An endpoint that is not an absolute http(s) URI ("foo", "alloy:4317" without the scheme) cannot be exported
        // to; treat it as telemetry off instead of crashing startup (new Uri("foo")) or exporting to a bogus scheme.
        private static Uri? HttpUri(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : null;
    }
}
