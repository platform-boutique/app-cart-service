using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace cartservice
{
    public sealed class JsonLogFormatterOptions : ConsoleFormatterOptions
    {
        public ServiceIdentity Identity { get; set; } = new("cart-service", ServiceIdentity.Dev, ServiceIdentity.Unknown, "local", "unknown");

        /// <summary>Injected for tests; the host clock is not used directly.</summary>
        public TimeProvider Clock { get; set; } = TimeProvider.System;
    }

    /// <summary>Console formatter "everly-json": writes <see cref="JsonLog"/> lines; trace ids come from Activity.Current.</summary>
    public sealed class JsonLogFormatter(IOptionsMonitor<JsonLogFormatterOptions> options) : ConsoleFormatter(FormatterName)
    {
        public const string FormatterName = "everly-json";

        public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
        {
            ArgumentNullException.ThrowIfNull(textWriter);

            JsonLogFormatterOptions current = options.CurrentValue;
            string message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception) ?? string.Empty;
            if (message.Length == 0 && logEntry.Exception is null)
            {
                return;
            }

            Dictionary<string, string> extra = [];
            string? requestId = null;

            if (logEntry.State is IEnumerable<KeyValuePair<string, object?>> state)
            {
                Collect(state, extra);
            }

            scopeProvider?.ForEachScope(
                static (scope, args) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        Collect(pairs, args);
                    }
                },
                extra);

            if (extra.Remove("request_id", out string? rid))
            {
                requestId = rid;
            }

            string line = JsonLog.Build(
                current.Clock.GetUtcNow(),
                logEntry.LogLevel,
                message,
                current.Identity,
                JsonLog.CurrentIds(),
                requestId,
                logEntry.Exception is null ? null : JsonLog.ErrorString(logEntry.Exception),
                extra);
            textWriter.WriteLine(line);
        }

        private static void Collect(IEnumerable<KeyValuePair<string, object?>> pairs, Dictionary<string, string> into)
        {
            foreach (KeyValuePair<string, object?> pair in pairs)
            {
                if (pair.Value is not null && JsonLog.AllowedFields.TryGetValue(pair.Key, out string? name))
                {
                    into[name] = Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
        }
    }
}
