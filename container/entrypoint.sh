#!/bin/sh
# Container entrypoint of cart-service (OBS-40, ADR 2): decides, BEFORE the .NET runtime starts, whether the Grafana
# Pyroscope .NET profiler is loaded. The CLR reads CORECLR_* once at process start, so the app cannot switch it on
# itself; this script does it from the same environment contract as every other Everly service:
#
#   profiling is ON only when PYROSCOPE_ADDR is an absolute http(s) URL with a host;
#   OTEL_SDK_DISABLED=true or ENABLE_PROFILING=0 force it OFF; unset, empty or malformed means OFF.
#
# It never fails: an unusable setting or a missing profiler file prints one JSON warning and the service starts
# without profiling. With profiling OFF nothing is exported: the service runs exactly as without this script.
# POSIX sh only (the image has dash and bash, no other shell tools are needed besides tr, date and dirname).
#
# The rule is mirrored by ProfilingConfig.PyroscopeAddress (src/ProfilingConfig.cs, used for the "starting" log
# line); tests/ProfilerEntrypointTests.cs runs this script and that function over the same matrix.

app_dir=$(dirname "$0")
profiler_dir=${PYROSCOPE_DOTNET_DIR:-/opt/pyroscope}   # where the Dockerfile installs the profiler (override: tests only)

trim() { printf '%s' "$1" | tr -d '\r' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//'; }

warn() {
    # one JSON object per line like every log line of the service (OBS-30); the message is ASCII without quotes
    printf '{"timestamp":"%s","severity":"warning","message":"%s","service":"%s","version":"%s"}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$1" "${service:-cart-service}" "${SERVICE_VERSION:-dev}"
}

# A label value for PYROSCOPE_LABELS ("key:value,key:value"): anything outside a safe set becomes "_".
label() { printf '%s' "$1" | tr -c 'A-Za-z0-9._+-' '_'; }

valid_http_url() {
    case "$1" in
        http://* | https://*) ;;
        *) return 1 ;;
    esac
    case "$1" in
        *[[:space:]]*) return 1 ;;
    esac
    rest=${1#*://}
    authority=${rest%%[/?#]*}
    host=${authority##*@}
    host=${host%%:*}
    [ -n "$host" ]
}

service=$(trim "${OTEL_SERVICE_NAME:-}")
service=${service:-cart-service}
addr=$(trim "${PYROSCOPE_ADDR:-}")
sdk_disabled=$(trim "${OTEL_SDK_DISABLED:-}" | tr 'A-Z' 'a-z')
enable_profiling=$(trim "${ENABLE_PROFILING:-}")

if [ "$sdk_disabled" = "true" ] || [ "$enable_profiling" = "0" ] || [ -z "$addr" ]; then
    : # profiling off
elif ! valid_http_url "$addr"; then
    warn "profiling off: PYROSCOPE_ADDR is not an absolute http(s) URL with a host"
elif [ ! -f "$profiler_dir/Pyroscope.Profiler.Native.so" ] || [ ! -f "$profiler_dir/Pyroscope.Linux.ApiWrapper.x64.so" ]; then
    warn "profiling off: the Pyroscope profiler files are missing from $profiler_dir"
else
    version=$(trim "${SERVICE_VERSION:-}")
    environment=$(trim "${DEPLOYMENT_ENVIRONMENT:-}")

    export PYROSCOPE_PROFILING_ENABLED=1
    export PYROSCOPE_SERVER_ADDRESS="$addr"
    export PYROSCOPE_APPLICATION_NAME="$service"
    # service_name is NOT a label here: the profiler derives it from the application name, and a second one makes
    # Pyroscope reject every profile ("duplicate label name: service_name", seen on 2026-11-22).
    export PYROSCOPE_LABELS="version:$(label "${version:-dev}"),environment:$(label "${environment:-local}")"
    # The profiler's own diagnostics: by default it prints text lines to stderr (they would break the one-JSON-object-per-line
    # log of the service) and writes a rotating log file under /var/log/pyroscope, which user 10001 cannot write. Keep stderr
    # quiet and write the file to /tmp (if /tmp is not writable the profiler goes on without a log file). Both can be overridden.
    export PYROSCOPE_LOG_STDERR="${PYROSCOPE_LOG_STDERR:-false}"
    export DD_TRACE_LOG_DIRECTORY="${DD_TRACE_LOG_DIRECTORY:-/tmp}"

    export CORECLR_ENABLE_PROFILING=1
    export CORECLR_PROFILER='{BD1A650D-AC5D-4896-B64F-D6FA25D6B26A}'
    export CORECLR_PROFILER_PATH="$profiler_dir/Pyroscope.Profiler.Native.so"
    export LD_PRELOAD="$profiler_dir/Pyroscope.Linux.ApiWrapper.x64.so${LD_PRELOAD:+:$LD_PRELOAD}"
    export LD_LIBRARY_PATH="$profiler_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

    # DOTNET_EnableDiagnostics=0 (set in the image) would also disable the profiler (.NET 8+): switch only the
    # profiler part back on and keep the diagnostics IPC port and the debugger off.
    export DOTNET_EnableDiagnostics=1
    export DOTNET_EnableDiagnostics_IPC=0
    export DOTNET_EnableDiagnostics_Debugger=0
    export DOTNET_EnableDiagnostics_Profiler=1
fi

exec "$app_dir/cartservice" "$@"
