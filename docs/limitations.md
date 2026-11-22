# Known limitations

- Carts never expire (no TTL) and `AddItem` is not atomic.
- Health is Redis-coupled; see probe guidance above.
- Logs are one JSON object per line on stdout through `JsonLogFormatter` (the one log shape of the standards); framework (`Microsoft.*`) categories stay at `warning` or above.
- Profiling (Pyroscope .NET profiler) is CPU only and has no link between profiles and traces. It is a native profiler in the process: it is amd64/glibc only (the image is amd64 only), the version is pinned in the Dockerfile and bumped by hand (Dependabot does not see a release download), and it is loaded only through the image entrypoint, so `dotnet run` and the unit tests never load it. See [ADR 2](adr/0002-profiling-with-the-pyroscope-dotnet-profiler.md).
- The entrypoint script and `ProfilingConfig` implement the same `PYROSCOPE_ADDR` rule twice (shell before the runtime starts, C# for the log line); `ProfilerEntrypointTests` runs both over the same matrix so they cannot drift unnoticed.
