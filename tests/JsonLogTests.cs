using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using cartservice;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace cartservice.tests
{
    /// <summary>Routes ILogger calls through the real JsonLogFormatter into a string, like the console provider does.</summary>
    internal sealed class FormatterLoggerProvider(JsonLogFormatter formatter, TextWriter output) : ILoggerProvider, ISupportExternalScope
    {
        private readonly JsonLogFormatter _formatter = formatter;
        private readonly TextWriter _output = output;
        private IExternalScopeProvider? _scopes;

        public ILogger CreateLogger(string categoryName) => new FormatterLogger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class FormatterLogger(FormatterLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                LogEntry<TState> entry = new(logLevel, category, eventId, state, exception, formatter);
                owner._formatter.Write(entry, owner._scopes, owner._output);
            }
        }
    }

    public sealed class JsonLogTests
    {
        private static readonly ServiceIdentity Id = new("cart-service", "1.2.3", "abc1234", "local", "pod-1");
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 20, 30, 123, TimeSpan.Zero);

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static (ILogger Logger, StringWriter Out, ILoggerFactory Factory) NewLogger()
        {
            JsonLogFormatterOptions options = new() { Identity = Id, Clock = new FixedClock(Now) };
            JsonLogFormatter formatter = new(new TestOptions(options));
            StringWriter output = new();
            ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new FormatterLoggerProvider(formatter, output)));
            return (factory.CreateLogger("test"), output, factory);
        }

        private sealed class TestOptions(JsonLogFormatterOptions value) : IOptionsMonitor<JsonLogFormatterOptions>
        {
            public JsonLogFormatterOptions CurrentValue => value;

            public JsonLogFormatterOptions Get(string? name) => value;

            public IDisposable? OnChange(Action<JsonLogFormatterOptions, string?> listener) => null;
        }

        private static JsonElement[] Lines(StringWriter output) =>
            [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

        [Fact]
        public void Golden_OutsideSpan_HasNoTraceIds()
        {
            (ILogger logger, StringWriter output, ILoggerFactory factory) = NewLogger();
            using (factory)
            {
                Assert.Null(Activity.Current);
#pragma warning disable CA1848, CA2254, CA1873
                logger.LogInformation("hello {Name}", "world");
#pragma warning restore CA1848, CA2254, CA1873
            }

            Assert.Equal(
                "{\"timestamp\":\"2026-11-14T10:20:30.123000Z\",\"severity\":\"info\",\"message\":\"hello world\",\"service\":\"cart-service\",\"version\":\"1.2.3\"}",
                output.ToString().TrimEnd());
        }

        [Fact]
        public void InsideSpan_HasHexTraceAndSpanId()
        {
            using ActivitySource source = new("test.logs");
            using ActivityListener listener = new()
            {
                ShouldListenTo = s => s.Name == "test.logs",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);

            (ILogger logger, StringWriter output, ILoggerFactory factory) = NewLogger();
            using (factory)
            using (Activity activity = source.StartActivity("op")!)
            {
#pragma warning disable CA1848
                logger.LogInformation("in span");
#pragma warning restore CA1848
                JsonElement line = Lines(output).Single();
                Assert.Equal(activity.TraceId.ToHexString(), line.GetProperty("trace_id").GetString());
                Assert.Equal(activity.SpanId.ToHexString(), line.GetProperty("span_id").GetString());
                Assert.Matches("^[0-9a-f]{32}$", line.GetProperty("trace_id").GetString());
                Assert.Matches("^[0-9a-f]{16}$", line.GetProperty("span_id").GetString());
            }

            // after the span ended the ids are gone again
            output.GetStringBuilder().Clear();
#pragma warning disable CA1848
            logger.LogInformation("after");
#pragma warning restore CA1848
            JsonElement after = Lines(output).Single();
            Assert.False(after.TryGetProperty("trace_id", out _));
            Assert.False(after.TryGetProperty("span_id", out _));
        }

        [Theory]
        [InlineData(LogLevel.Trace, "debug")]
        [InlineData(LogLevel.Debug, "debug")]
        [InlineData(LogLevel.Information, "info")]
        [InlineData(LogLevel.Warning, "warning")]
        [InlineData(LogLevel.Error, "error")]
        [InlineData(LogLevel.Critical, "error")]
        public void SeverityNames(LogLevel level, string expected) => Assert.Equal(expected, JsonLog.Severity(level));

        [Fact]
        public void Exception_BecomesErrorString_WithoutStackTrace()
        {
            (ILogger logger, StringWriter output, ILoggerFactory factory) = NewLogger();
            using (factory)
            {
#pragma warning disable CA1848
                InvalidOperationException ex = new("redis down");
                logger.LogError(ex, "Failed to get cart");
#pragma warning restore CA1848
            }

            JsonElement line = Lines(output).Single();
            Assert.Equal("error", line.GetProperty("severity").GetString());
            Assert.Equal("InvalidOperationException: redis down", line.GetProperty("error").GetString());
            Assert.DoesNotContain("   at ", output.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyAllowListedStateFieldsAreWritten()
        {
            (ILogger logger, StringWriter output, ILoggerFactory factory) = NewLogger();
            using (factory)
            {
#pragma warning disable CA1848, CA1873
                logger.LogInformation("card {CardNumber} email {Email} commit {Commit}", "4111111111111111", "a@b.c", "abc");
#pragma warning restore CA1848, CA1873
            }

            JsonElement line = Lines(output).Single();
            string[] names = [.. line.EnumerateObject().Select(p => p.Name)];
            Assert.Equal(["timestamp", "severity", "message", "service", "version", "commit"], names);
        }

        [Fact]
        public async Task RequestIdHeader_IsOnEveryLineOfThatCall_AndAbsentWithoutHeader()
        {
            (ILogger logger, StringWriter output, ILoggerFactory factory) = NewLogger();
            using (factory)
            {
                RequestIdInterceptor interceptor = new(factory.CreateLogger<RequestIdInterceptor>());

                Task<string> Handler(string req, ServerCallContext ctx)
                {
#pragma warning disable CA1848
                    logger.LogInformation("handling");
#pragma warning restore CA1848
                    return Task.FromResult(req);
                }

                _ = await interceptor.UnaryServerHandler("x", TestServerCallContext.Create(new Metadata { { RequestIdInterceptor.Header, "req-123" } }), Handler);
                _ = await interceptor.UnaryServerHandler("x", TestServerCallContext.Create(), Handler);
            }

            JsonElement[] lines = Lines(output);
            Assert.Equal("req-123", lines[0].GetProperty("request_id").GetString());
            Assert.False(lines[1].TryGetProperty("request_id", out _));
        }

        [Theory]
        [InlineData("req-123", "req-123")]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("has space", null)]
        [InlineData("line\nbreak", null)]
        public void RequestIdValidation(string? value, string? expected) => Assert.Equal(expected, RequestIdInterceptor.Valid(value));

        [Fact]
        public void StartupError_IsOneJsonLine()
        {
            using StringWriter output = new();
            JsonLog.WriteStartupError(new InvalidOperationException("REDIS_ADDR environment variable is not set."), Id, output);

            string text = output.ToString();
            Assert.Single(text.TrimEnd().Split('\n'));
            JsonElement line = JsonDocument.Parse(text).RootElement;
            Assert.Equal("error", line.GetProperty("severity").GetString());
            Assert.Equal("startup failed", line.GetProperty("message").GetString());
            Assert.Equal("cart-service", line.GetProperty("service").GetString());
            Assert.Equal("InvalidOperationException: REDIS_ADDR environment variable is not set.", line.GetProperty("error").GetString());
        }

        [Fact]
        public void Timestamp_IsRfc3339Utc_WithFractionalSeconds() =>
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", JsonLog.Timestamp(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))));
    }
}
