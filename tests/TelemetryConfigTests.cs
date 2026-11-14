using cartservice;
using Xunit;

namespace cartservice.tests
{
    public class TelemetryConfigTests
    {
        private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        {
            Dictionary<string, string> map = vars.ToDictionary(v => v.Name, v => v.Value);
            return name => map.TryGetValue(name, out string? value) ? value : null;
        }

        [Fact]
        public void Unset_IsOff() => Assert.Null(TelemetryConfig.OtlpEndpoint(Env()));

        [Fact]
        public void EmptyEndpoint_IsOff_AndDoesNotThrow()
        {
            // Regression: an empty OTEL_EXPORTER_OTLP_ENDPOINT used to crash startup with new Uri("").
            Assert.Null(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", ""))));
        }

        [Fact]
        public void Endpoint_IsUsed() =>
            Assert.Equal(new Uri("http://alloy:4317"), TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://alloy:4317"))));

        [Fact]
        public void LegacyCollectorAddr_IsIgnored() =>
            Assert.Null(TelemetryConfig.OtlpEndpoint(Env(("COLLECTOR_SERVICE_ADDR", "alloy:4317"))));

        [Fact]
        public void Endpoint_IsNotAffectedByLegacyCollectorAddr() =>
            Assert.Equal(
                new Uri("http://a:4317"),
                TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://a:4317"), ("COLLECTOR_SERVICE_ADDR", "b:4317"))));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://")]
        [InlineData("ftp://alloy:4317")]
        [InlineData("not a url")]
        public void EmptyOrMalformed_IsOff_AndDoesNotThrow(string value) =>
            Assert.Null(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", value))));

        [Theory]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData(" True ")]
        public void SdkDisabled_ForcesOff(string value) =>
            Assert.Null(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://alloy:4317"), ("OTEL_SDK_DISABLED", value))));

        [Fact]
        public void SdkDisabledFalse_KeepsOn() =>
            Assert.NotNull(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://alloy:4317"), ("OTEL_SDK_DISABLED", "false"))));

        [Theory]
        [InlineData("foo")]
        [InlineData("alloy:4317")]
        [InlineData("grpc://alloy:4317")]
        public void NotAnHttpUri_IsOff_AndDoesNotThrow(string value) =>
            Assert.Null(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", value))));

        [Fact]
        public void HttpsEndpoint_IsUsed() =>
            Assert.Equal(new Uri("https://alloy:4317"), TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "https://alloy:4317"))));

        [Fact]
        public void LegacyEnableTracing0_IsIgnored() =>
            Assert.NotNull(TelemetryConfig.OtlpEndpoint(Env(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://alloy:4317"), ("ENABLE_TRACING", "0"))));
    }
}
