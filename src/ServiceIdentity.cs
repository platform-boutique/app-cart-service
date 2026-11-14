using System.Reflection;

namespace cartservice
{
    /// <summary>
    /// Who this process is, on every signal (OBS-02, CODE-06): name, version, commit, environment, instance id.
    /// Pure functions of the environment so the matrix is unit tested.
    /// </summary>
    public sealed record ServiceIdentity(string Name, string Version, string Commit, string Environment, string InstanceId)
    {
        public const string Namespace = "everly";
        public const string DefaultName = "cart-service";
        public const string Dev = "dev";
        public const string Unknown = "unknown";

        /// <summary>The version the csproj carries when the build did not pass -p:Version; it means "no real version".</summary>
        public const string PlaceholderAssemblyVersion = "0.0.0-dev";

        public static ServiceIdentity From(Func<string, string?> getEnv, string? assemblyInformationalVersion, string machineName)
        {
            ArgumentNullException.ThrowIfNull(getEnv);

            string Value(string name) => (getEnv(name) ?? string.Empty).Trim();
            static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

            return new ServiceIdentity(
                Name: Or(Value("OTEL_SERVICE_NAME"), DefaultName),
                Version: ResolveVersion(Value("SERVICE_VERSION"), assemblyInformationalVersion),
                Commit: Or(Value("SERVICE_COMMIT"), Unknown),
                Environment: Or(Value("DEPLOYMENT_ENVIRONMENT"), "local"),
                // In Kubernetes the hostname is the pod name, which keeps replicas distinct.
                InstanceId: Or(Value("HOSTNAME"), machineName));
        }

        /// <summary>Production entry point: reads the process environment and this assembly's build info.</summary>
        public static ServiceIdentity FromProcess() =>
            From(
                System.Environment.GetEnvironmentVariable,
                typeof(ServiceIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                System.Environment.MachineName);

        /// <summary>SERVICE_VERSION, else the assembly informational version (without "+build metadata"), else <c>dev</c>.</summary>
        public static string ResolveVersion(string? serviceVersionEnv, string? assemblyInformationalVersion)
        {
            string fromEnv = (serviceVersionEnv ?? string.Empty).Trim();
            if (fromEnv.Length > 0)
            {
                return fromEnv;
            }

            string fromAssembly = (assemblyInformationalVersion ?? string.Empty).Split('+')[0].Trim();
            return fromAssembly.Length == 0 || fromAssembly == PlaceholderAssemblyVersion ? Dev : fromAssembly;
        }
    }
}
