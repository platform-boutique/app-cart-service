namespace cartservice
{
    /// <summary>
    /// Continuous profiling contract (OBS-40, same as the other Everly services): the Grafana Pyroscope .NET profiler
    /// is ON only when PYROSCOPE_ADDR is an absolute http(s) URL with a host; OTEL_SDK_DISABLED=true or
    /// ENABLE_PROFILING=0 force it off; unset, empty or malformed means off (never a crash).
    /// The profiler is a native CLR profiler: the runtime reads CORECLR_* once at process start, so the decision that
    /// really switches it on is made by container/entrypoint.sh (the same rule, tested against this function in
    /// ProfilerEntrypointTests). This class only tells the "starting" log line what the environment asked for and
    /// whether the runtime actually got the profiler.
    /// </summary>
    public static class ProfilingConfig
    {
        /// <summary>The CLSID of the Pyroscope .NET profiler (CORECLR_PROFILER).</summary>
        public const string ProfilerClsid = "{BD1A650D-AC5D-4896-B64F-D6FA25D6B26A}";

        /// <summary>The Pyroscope server address, or <c>null</c> when profiling is off.</summary>
        public static Uri? PyroscopeAddress(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);

            string Value(string name) => (getEnv(name) ?? string.Empty).Trim();

            if (string.Equals(Value("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase)
                || Value("ENABLE_PROFILING") == "0")
            {
                return null;
            }

            string addr = Value("PYROSCOPE_ADDR");
            return addr.Length > 0
                && Uri.TryCreate(addr, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.Host.Length > 0
                    ? uri
                    : null;
        }

        /// <summary>True when this process was started with the Pyroscope profiler loaded by the CLR.</summary>
        public static bool ProfilerLoaded(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);
            return getEnv("CORECLR_ENABLE_PROFILING") == "1"
                && string.Equals(getEnv("CORECLR_PROFILER"), ProfilerClsid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The value of the "profiling" field of the "starting" log line.</summary>
        public static string Describe(Func<string, string?> getEnv)
        {
            Uri? address = PyroscopeAddress(getEnv);
            if (address is null)
            {
                return "off";
            }

            return ProfilerLoaded(getEnv)
                ? $"on (Pyroscope {address})"
                : $"requested ({address}) but the profiler is not loaded (see the entrypoint warning)";
        }
    }
}
