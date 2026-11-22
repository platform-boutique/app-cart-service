using System.Diagnostics;
using System.Runtime.InteropServices;
using cartservice;
using Xunit;

namespace cartservice.tests
{
    /// <summary>
    /// Runs the real container/entrypoint.sh (POSIX sh) in a temp directory with a stub "cartservice" that prints its
    /// environment and arguments, and a fake profiler directory. Proves the switch: profiling is ON only for a valid
    /// PYROSCOPE_ADDR, never changes anything when OFF, never fails, and agrees with ProfilingConfig.PyroscopeAddress.
    /// </summary>
    public sealed class ProfilerEntrypointTests : IDisposable
    {
        private const string Addr = "http://pyroscope:4040";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cart-entrypoint-" + Guid.NewGuid().ToString("N"));
        private readonly string _profilerDir;

        public ProfilerEntrypointTests()
        {
            _profilerDir = Path.Combine(_dir, "profiler");
            _ = Directory.CreateDirectory(_profilerDir);
            File.WriteAllText(Path.Combine(_profilerDir, "Pyroscope.Profiler.Native.so"), string.Empty);
            File.WriteAllText(Path.Combine(_profilerDir, "Pyroscope.Linux.ApiWrapper.x64.so"), string.Empty);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.Copy(EntrypointPath(), Path.Combine(_dir, "entrypoint.sh"));
                File.WriteAllText(Path.Combine(_dir, "cartservice"), "#!/bin/sh\necho \"ARGS=$*\"\nenv\nexit 7\n");
                File.SetUnixFileMode(Path.Combine(_dir, "cartservice"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private static string EntrypointPath()
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "container", "entrypoint.sh");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("container/entrypoint.sh not found above " + AppContext.BaseDirectory);
        }

        private sealed record Result(int ExitCode, Dictionary<string, string> Env, string Args, string Stdout);

        private Result Run(IEnumerable<(string Name, string Value)> vars, string profilerDir, params string[] args)
        {
            Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "the entrypoint is a Linux container script");

            ProcessStartInfo psi = new("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(Path.Combine(_dir, "entrypoint.sh"));
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }

            // A clean environment: only PATH, the profiler directory override and what the case sets.
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            psi.Environment["PYROSCOPE_DOTNET_DIR"] = profilerDir;
            foreach ((string name, string value) in vars)
            {
                psi.Environment[name] = value;
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            p.WaitForExit();

            Dictionary<string, string> env = [];
            string argsLine = string.Empty;
            foreach (string line in stdout.Split('\n'))
            {
                int eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }

                string key = line[..eq];
                if (key == "ARGS")
                {
                    argsLine = line[(eq + 1)..];
                }
                else
                {
                    env[key] = line[(eq + 1)..];
                }
            }

            return new Result(p.ExitCode, env, argsLine, stdout);
        }

        private Result Run(params (string Name, string Value)[] vars) => Run(vars, _profilerDir);

        private static void AssertOff(Result r)
        {
            Assert.Equal(7, r.ExitCode); // the stub's exit code: the script execs the app and does not swallow it
            foreach (string key in new[]
            {
                "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "LD_PRELOAD", "LD_LIBRARY_PATH",
                "PYROSCOPE_PROFILING_ENABLED", "PYROSCOPE_SERVER_ADDRESS", "PYROSCOPE_APPLICATION_NAME", "PYROSCOPE_LABELS",
                "PYROSCOPE_LOG_STDERR", "DD_TRACE_LOG_DIRECTORY",
            })
            {
                Assert.False(r.Env.ContainsKey(key), key + " must not be set when profiling is off");
            }
        }

        [SkippableFact]
        public void Unset_IsOff_AndLeavesTheDiagnosticsSettingAlone()
        {
            Result r = Run(("DOTNET_EnableDiagnostics", "0"));
            AssertOff(r);
            Assert.Equal("0", r.Env["DOTNET_EnableDiagnostics"]);
            Assert.DoesNotContain("warning", r.Stdout, StringComparison.Ordinal);
        }

        [SkippableFact]
        public void ValidAddress_LoadsTheProfilerWithTheServiceIdentity()
        {
            Result r = Run(
                ("PYROSCOPE_ADDR", Addr),
                ("SERVICE_VERSION", "1.2.3"),
                ("DEPLOYMENT_ENVIRONMENT", "staging"),
                ("DOTNET_EnableDiagnostics", "0"));

            Assert.Equal(7, r.ExitCode);
            Assert.Equal("1", r.Env["CORECLR_ENABLE_PROFILING"]);
            Assert.Equal(ProfilingConfig.ProfilerClsid, r.Env["CORECLR_PROFILER"]);
            Assert.Equal(Path.Combine(_profilerDir, "Pyroscope.Profiler.Native.so"), r.Env["CORECLR_PROFILER_PATH"]);
            Assert.Equal(Path.Combine(_profilerDir, "Pyroscope.Linux.ApiWrapper.x64.so"), r.Env["LD_PRELOAD"]);
            Assert.Equal(_profilerDir, r.Env["LD_LIBRARY_PATH"]);
            Assert.Equal("1", r.Env["PYROSCOPE_PROFILING_ENABLED"]);
            Assert.Equal(Addr, r.Env["PYROSCOPE_SERVER_ADDRESS"]);
            Assert.Equal("cart-service", r.Env["PYROSCOPE_APPLICATION_NAME"]);
            Assert.Equal("version:1.2.3,environment:staging", r.Env["PYROSCOPE_LABELS"]);
            Assert.Equal("false", r.Env["PYROSCOPE_LOG_STDERR"]);
            Assert.Equal("/tmp", r.Env["DD_TRACE_LOG_DIRECTORY"]);

            // DOTNET_EnableDiagnostics=0 would disable the profiler: only the profiler part is switched back on.
            Assert.Equal("1", r.Env["DOTNET_EnableDiagnostics"]);
            Assert.Equal("0", r.Env["DOTNET_EnableDiagnostics_IPC"]);
            Assert.Equal("0", r.Env["DOTNET_EnableDiagnostics_Debugger"]);
            Assert.Equal("1", r.Env["DOTNET_EnableDiagnostics_Profiler"]);
        }

        [SkippableFact]
        public void ServiceNameAndDefaults_FollowTheIdentityRules()
        {
            Result named = Run(("PYROSCOPE_ADDR", Addr), ("OTEL_SERVICE_NAME", " cart-two "), ("SERVICE_VERSION", "a b,c:d"));
            Assert.Equal("cart-two", named.Env["PYROSCOPE_APPLICATION_NAME"]);
            Assert.Equal("version:a_b_c_d,environment:local", named.Env["PYROSCOPE_LABELS"]);
            Assert.DoesNotContain("service_name", named.Env["PYROSCOPE_LABELS"], StringComparison.Ordinal); // the profiler adds it from the application name

            Result bare = Run(("PYROSCOPE_ADDR", Addr));
            Assert.Equal("version:dev,environment:local", bare.Env["PYROSCOPE_LABELS"]);
        }

        [SkippableFact]
        public void ArgumentsAreForwardedToTheService()
        {
            Result r = Run([("PYROSCOPE_ADDR", Addr)], _profilerDir, "--urls", "http://*:1");
            Assert.Equal("--urls http://*:1", r.Args);
        }

        [SkippableFact]
        public void MissingProfilerFiles_StartTheServiceWithoutProfiling_AndWarnAsJson()
        {
            Result r = Run([("PYROSCOPE_ADDR", Addr)], Path.Combine(_dir, "does-not-exist"));
            AssertOff(r);
            Assert.Contains("\"severity\":\"warning\"", r.Stdout, StringComparison.Ordinal);
            Assert.Contains("profiler files are missing", r.Stdout, StringComparison.Ordinal);
        }

        [SkippableFact]
        public void MalformedAddress_StartsWithoutProfiling_AndWarns()
        {
            Result r = Run(("PYROSCOPE_ADDR", "pyroscope:4040"));
            AssertOff(r);
            Assert.Contains("not an absolute http(s) URL", r.Stdout, StringComparison.Ordinal);
        }

        // The same matrix as the other Everly services; every row must give the same answer in the script and in C#.
        public static TheoryData<string, string?, string?, string?, bool> Matrix => new()
        {
            { "unset", null, null, null, false },
            { "empty", "", null, null, false },
            { "blank", "   ", null, null, false },
            { "valid http", Addr, null, null, true },
            { "valid https", "https://pyroscope.example:4040", null, null, true },
            { "valid with path", "http://pyroscope:4040/", null, null, true },
            { "trimmed", "  http://pyroscope:4040  ", null, null, true },
            { "enable_profiling=1", Addr, null, "1", true },
            { "enable_profiling=0", Addr, null, "0", false },
            { "enable_profiling=1 alone", null, null, "1", false },
            { "no scheme", "pyroscope:4040", null, null, false },
            { "scheme only", "http://", null, null, false },
            { "no host", "http://:4040", null, null, false },
            { "other scheme", "ftp://pyroscope:4040", null, null, false },
            { "with space", "http://pyro scope:4040", null, null, false },
            { "garbage", "not a url", null, null, false },
            { "sdk disabled", Addr, "true", null, false },
            { "sdk disabled, spaces and case", Addr, " True ", null, false },
            { "sdk disabled=false", Addr, "false", null, true },
        };

        [SkippableTheory]
        [MemberData(nameof(Matrix))]
        public void Script_AndConfigFunction_AgreeOnTheMatrix(string name, string? addr, string? sdkDisabled, string? enableProfiling, bool expectedOn)
        {
            Dictionary<string, string> vars = [];
            if (addr is not null)
            {
                vars["PYROSCOPE_ADDR"] = addr;
            }

            if (sdkDisabled is not null)
            {
                vars["OTEL_SDK_DISABLED"] = sdkDisabled;
            }

            if (enableProfiling is not null)
            {
                vars["ENABLE_PROFILING"] = enableProfiling;
            }

            Result r = Run(vars.Select(kv => (kv.Key, kv.Value)), _profilerDir);

            Assert.Equal(7, r.ExitCode);
            Assert.True(expectedOn == r.Env.ContainsKey("CORECLR_ENABLE_PROFILING"), $"{name}: script decision");
            Assert.True(
                expectedOn == (ProfilingConfig.PyroscopeAddress(n => vars.TryGetValue(n, out string? v) ? v : null) is not null),
                $"{name}: ProfilingConfig decision");
        }
    }
}
