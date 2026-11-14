using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace cartservice
{
    /// <summary>
    /// The one log line shape (OBS-30, same as the Go services): a single JSON object per line with
    /// timestamp (RFC3339 UTC), severity (debug|info|warning|error), message, service, version and, only when present,
    /// trace_id/span_id, request_id, error and the allow-listed extra fields.
    /// </summary>
    public static class JsonLog
    {
        /// <summary>Extra structured fields that may reach the line: state key (case-insensitive) to output name. Everything else stays out (no PII by accident).</summary>
        public static readonly IReadOnlyDictionary<string, string> AllowedFields =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["request_id"] = "request_id",
                ["RequestId"] = "request_id",
                ["commit"] = "commit",
                ["Commit"] = "commit",
                ["port"] = "port",
                ["Port"] = "port",
                ["telemetry"] = "telemetry",
                ["Telemetry"] = "telemetry",
            };

        public static string Severity(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warning",
            _ => "error",
        };

        public static string Timestamp(DateTimeOffset now) =>
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

        /// <summary>The error string for an exception: type and message, never the stack trace.</summary>
        public static string ErrorString(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return $"{exception.GetType().Name}: {exception.Message}";
        }

        /// <summary>Hex ids of the current activity, or null outside a span (never empty or all zeros).</summary>
        public static (string TraceId, string SpanId)? CurrentIds()
        {
            Activity? activity = Activity.Current;
            if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || activity.TraceId == default || activity.SpanId == default)
            {
                return null;
            }

            return (activity.TraceId.ToHexString(), activity.SpanId.ToHexString());
        }

        public static string Build(
            DateTimeOffset now,
            LogLevel level,
            string message,
            ServiceIdentity identity,
            (string TraceId, string SpanId)? ids,
            string? requestId,
            string? error,
            IEnumerable<KeyValuePair<string, string>>? extra = null)
        {
            ArgumentNullException.ThrowIfNull(identity);

            using MemoryStream stream = new();
            // Relaxed escaping keeps quotes and plus signs readable; the output is a log line, never embedded in HTML.
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", Timestamp(now));
                writer.WriteString("severity", Severity(level));
                writer.WriteString("message", message);
                writer.WriteString("service", identity.Name);
                writer.WriteString("version", identity.Version);
                if (ids is { } i)
                {
                    writer.WriteString("trace_id", i.TraceId);
                    writer.WriteString("span_id", i.SpanId);
                }

                if (!string.IsNullOrEmpty(requestId))
                {
                    writer.WriteString("request_id", requestId);
                }

                if (!string.IsNullOrEmpty(error))
                {
                    writer.WriteString("error", error);
                }

                if (extra is not null)
                {
                    foreach (KeyValuePair<string, string> kv in extra)
                    {
                        writer.WriteString(kv.Key, kv.Value);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>An uncaught startup error as a JSON line on stdout (the process logger may not exist yet).</summary>
        public static void WriteStartupError(Exception exception, ServiceIdentity identity, TextWriter output)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(output);
            output.WriteLine(Build(DateTimeOffset.UtcNow, LogLevel.Error, "startup failed", identity, null, null, ErrorString(exception)));
            output.Flush();
        }
    }
}
