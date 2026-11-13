namespace cartservice
{
    /// <summary>
    /// Telemetry contract (same as the Go services): traces, metrics and logs are exported over OTLP/gRPC only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is an absolute http(s) URL; empty or malformed means off (never a crash);
    /// OTEL_SDK_DISABLED=true (trimmed, case-insensitive) forces it off. There is no localhost default.
    /// The legacy COLLECTOR_SERVICE_ADDR and ENABLE_TRACING are no longer read.
    /// </summary>
    public static class TelemetryConfig
    {
        /// <summary>The OTLP/gRPC endpoint, or <c>null</c> when telemetry is off.</summary>
        public static Uri? OtlpEndpoint(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);

            string Value(string name) => (getEnv(name) ?? string.Empty).Trim();

            if (string.Equals(Value("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)
               )
            {
                return null;
            }

            string endpoint = Value("OTEL_EXPORTER_OTLP_ENDPOINT");
            return endpoint.Length > 0 ? HttpUri(endpoint) : null;
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
