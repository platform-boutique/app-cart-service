namespace cartservice.store
{
    /// <summary>Limits of the cart storage layer.</summary>
    public sealed class CartStoreOptions
    {
        public const int DefaultRedisTimeoutMs = 2000;
        public const int MinRedisTimeoutMs = 50;
        public const int MaxRedisTimeoutMs = 30000;

        /// <summary>Upper bound for the Redis work of ONE cart RPC (all Redis round trips of AddItem together). The caller's own deadline still wins when it is shorter.</summary>
        public TimeSpan RedisTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultRedisTimeoutMs);

        /// <summary>
        /// Reads <c>REDIS_TIMEOUT_MS</c>. Unset, empty, not a number or outside
        /// [<see cref="MinRedisTimeoutMs"/>, <see cref="MaxRedisTimeoutMs"/>] means the default: a typo never disables the deadline.
        /// </summary>
        public static CartStoreOptions FromEnvironment(Func<string, string?> getenv)
        {
            ArgumentNullException.ThrowIfNull(getenv);
            bool ok = int.TryParse(getenv("REDIS_TIMEOUT_MS")?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int ms)
                && ms is >= MinRedisTimeoutMs and <= MaxRedisTimeoutMs;
            return new CartStoreOptions { RedisTimeout = TimeSpan.FromMilliseconds(ok ? ms : DefaultRedisTimeoutMs) };
        }
    }
}
