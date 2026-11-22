using cartservice;
using Xunit;

namespace cartservice.tests
{
    public class ProfilingConfigTests
    {
        private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        {
            Dictionary<string, string> map = vars.ToDictionary(v => v.Name, v => v.Value);
            return name => map.TryGetValue(name, out string? value) ? value : null;
        }

        [Fact]
        public void Unset_IsOff() => Assert.Null(ProfilingConfig.PyroscopeAddress(Env()));

        [Fact]
        public void ValidAddress_IsOn() =>
            Assert.Equal(new Uri("http://pyroscope:4040"), ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", "http://pyroscope:4040"))));

        [Fact]
        public void AddressIsTrimmed() =>
            Assert.NotNull(ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", "  https://pyroscope.example:4040 "))));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("pyroscope:4040")]
        [InlineData("http://")]
        [InlineData("ftp://pyroscope:4040")]
        [InlineData("not a url")]
        public void EmptyOrMalformed_IsOff_AndDoesNotThrow(string value) =>
            Assert.Null(ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", value))));

        [Theory]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData(" True ")]
        public void SdkDisabled_ForcesOff(string value) =>
            Assert.Null(ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", "http://pyroscope:4040"), ("OTEL_SDK_DISABLED", value))));

        [Fact]
        public void SdkDisabledFalse_KeepsOn() =>
            Assert.NotNull(ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", "http://pyroscope:4040"), ("OTEL_SDK_DISABLED", "false"))));

        [Fact]
        public void EnableProfilingZero_ForcesOff() =>
            Assert.Null(ProfilingConfig.PyroscopeAddress(Env(("PYROSCOPE_ADDR", "http://pyroscope:4040"), ("ENABLE_PROFILING", "0"))));

        [Fact]
        public void EnableProfilingOne_AloneStartsNothing() =>
            Assert.Null(ProfilingConfig.PyroscopeAddress(Env(("ENABLE_PROFILING", "1"))));

        [Fact]
        public void Describe_Off_WhenNoAddress() => Assert.Equal("off", ProfilingConfig.Describe(Env()));

        [Fact]
        public void Describe_On_WhenTheRuntimeLoadedTheProfiler() =>
            Assert.Equal(
                "on (Pyroscope http://pyroscope:4040/)",
                ProfilingConfig.Describe(Env(
                    ("PYROSCOPE_ADDR", "http://pyroscope:4040"),
                    ("CORECLR_ENABLE_PROFILING", "1"),
                    ("CORECLR_PROFILER", "{bd1a650d-ac5d-4896-b64f-d6fa25d6b26a}"))));

        [Fact]
        public void Describe_SaysSoWhenRequestedButNotLoaded() =>
            Assert.StartsWith(
                "requested (http://pyroscope:4040/) but the profiler is not loaded",
                ProfilingConfig.Describe(Env(("PYROSCOPE_ADDR", "http://pyroscope:4040"))));

        [Fact]
        public void ProfilerLoaded_NeedsBothVariables()
        {
            Assert.False(ProfilingConfig.ProfilerLoaded(Env(("CORECLR_ENABLE_PROFILING", "1"))));
            Assert.False(ProfilingConfig.ProfilerLoaded(Env(("CORECLR_PROFILER", ProfilingConfig.ProfilerClsid))));
            Assert.False(ProfilingConfig.ProfilerLoaded(Env(("CORECLR_ENABLE_PROFILING", "1"), ("CORECLR_PROFILER", "{OTHER}"))));
            Assert.True(ProfilingConfig.ProfilerLoaded(Env(("CORECLR_ENABLE_PROFILING", "1"), ("CORECLR_PROFILER", ProfilingConfig.ProfilerClsid))));
        }
    }
}
