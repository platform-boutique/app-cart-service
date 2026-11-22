using System.Net;
using System.Net.Sockets;

namespace cartservice
{
    /// <summary>
    /// <c>cartservice healthcheck</c>: the container HEALTHCHECK (CODE-03). A TCP connect to the gRPC port on the loopback
    /// interface, exit code 0 when the port accepts the connection and 1 otherwise. It does not look at Redis (the gRPC
    /// health service does, for readiness probes) so a Redis outage does not mark the container unhealthy. It needs no
    /// shell, nc or curl, and it starts without the entrypoint, so the profiler is never loaded into the probe.
    /// </summary>
    public static class HealthProbe
    {
        public const string Command = "healthcheck";
        public const int DefaultPort = 7070;
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        /// <summary>The first port of ASPNETCORE_HTTP_PORTS (the image sets 7070); anything unusable means 7070.</summary>
        public static int Port(Func<string, string?> getEnv)
        {
            ArgumentNullException.ThrowIfNull(getEnv);
            string first = (getEnv("ASPNETCORE_HTTP_PORTS") ?? string.Empty).Split(';', ',')[0].Trim();
            return int.TryParse(first, out int port) && port is > 0 and <= 65535 ? port : DefaultPort;
        }

        public static int Run(int port, TimeSpan timeout)
        {
            try
            {
                using TcpClient client = new();
                using CancellationTokenSource cts = new(timeout);
                client.ConnectAsync(IPAddress.Loopback, port, cts.Token).AsTask().GetAwaiter().GetResult();
                return 0;
            }
#pragma warning disable CA1031 // any failure (refused, timeout, unreachable) just means "not healthy"
            catch (Exception)
#pragma warning restore CA1031
            {
                return 1;
            }
        }
    }
}
