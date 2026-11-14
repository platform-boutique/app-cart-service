namespace cartservice
{
    /// <summary>LOG_LEVEL handling: debug, info, warn/warning, error (case-insensitive). Anything else falls back to info.</summary>
    public static class LogSettings
    {
        public static LogLevel Level(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);

            return (getEnv("LOG_LEVEL") ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "DEBUG" or "TRACE" => LogLevel.Debug,
                "WARN" or "WARNING" => LogLevel.Warning,
                "ERROR" or "FATAL" or "CRITICAL" => LogLevel.Error,
                _ => LogLevel.Information,
            };
        }
    }
}
