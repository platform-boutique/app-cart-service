using Microsoft.Extensions.Logging;
using cartservice;
using Xunit;

namespace cartservice.tests
{
    public class ServiceIdentityTests
    {
        private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        {
            Dictionary<string, string> map = vars.ToDictionary(v => v.Name, v => v.Value);
            return name => map.TryGetValue(name, out string? value) ? value : null;
        }

        [Fact]
        public void Defaults()
        {
            ServiceIdentity id = ServiceIdentity.From(Env(), null, "my-host");
            Assert.Equal("cart-service", id.Name);
            Assert.Equal("dev", id.Version);
            Assert.Equal("unknown", id.Commit);
            Assert.Equal("local", id.Environment);
            Assert.Equal("my-host", id.InstanceId);
        }

        [Fact]
        public void EnvOverridesEverything()
        {
            ServiceIdentity id = ServiceIdentity.From(
                Env(
                    ("OTEL_SERVICE_NAME", "cart-2"),
                    ("SERVICE_VERSION", " 2.3.4 "),
                    ("SERVICE_COMMIT", "abc1234"),
                    ("DEPLOYMENT_ENVIRONMENT", "prod"),
                    ("HOSTNAME", "cart-7d9f")),
                "9.9.9",
                "my-host");
            Assert.Equal(new ServiceIdentity("cart-2", "2.3.4", "abc1234", "prod", "cart-7d9f"), id);
        }

        [Theory]
        [InlineData(null, null, "dev")]
        [InlineData("", "", "dev")]
        [InlineData("  ", "0.0.0-dev", "dev")]                // placeholder from the csproj means "no real version"
        [InlineData(null, "1.4.0", "1.4.0")]                  // assembly informational version
        [InlineData(null, "1.4.0+0a1b2c3d4e", "1.4.0")]       // build metadata stripped
        [InlineData("2.0.0", "1.4.0", "2.0.0")]               // SERVICE_VERSION wins
        public void VersionChain(string? env, string? assembly, string expected) =>
            Assert.Equal(expected, ServiceIdentity.ResolveVersion(env, assembly));

        [Fact]
        public void ThisAssembly_HasNoDefault100Version() =>
            // the old default "1.0.0" is gone: a build without -p:Version reports dev
            Assert.Equal("dev", ServiceIdentity.From(Env(), typeof(ServiceIdentity).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion, "h").Version);
    }

    public class LogSettingsTests
    {
        [Theory]
        [InlineData(null, LogLevel.Information)]
        [InlineData("", LogLevel.Information)]
        [InlineData("info", LogLevel.Information)]
        [InlineData("INFO", LogLevel.Information)]
        [InlineData("debug", LogLevel.Debug)]
        [InlineData(" Debug ", LogLevel.Debug)]
        [InlineData("warn", LogLevel.Warning)]
        [InlineData("warning", LogLevel.Warning)]
        [InlineData("error", LogLevel.Error)]
        [InlineData("nonsense", LogLevel.Information)]
        public void LogLevelMatrix(string? value, LogLevel expected) =>
            Assert.Equal(expected, LogSettings.Level(name => name == "LOG_LEVEL" ? value : null));
    }
}
