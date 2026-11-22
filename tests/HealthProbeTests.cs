using System.Net;
using System.Net.Sockets;
using cartservice;
using Xunit;

namespace cartservice.tests
{
    public class HealthProbeTests
    {
        private static Func<string, string?> Env(string? ports) => name => name == "ASPNETCORE_HTTP_PORTS" ? ports : null;

        [Theory]
        [InlineData(null, 7070)]
        [InlineData("", 7070)]
        [InlineData("7070", 7070)]
        [InlineData("8080;9090", 8080)]
        [InlineData(" 8081 , 9090", 8081)]
        [InlineData("abc", 7070)]
        [InlineData("0", 7070)]
        [InlineData("70000", 7070)]
        public void Port_IsTheFirstUsableOne_ElseTheDefault(string? value, int expected) =>
            Assert.Equal(expected, HealthProbe.Port(Env(value)));

        [Fact]
        public void Run_ReturnsZero_WhenThePortAcceptsConnections()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Equal(0, HealthProbe.Run(port, TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void Run_ReturnsOne_WhenNothingListens()
        {
            // Reserve a free port, then close it: connecting is refused at once.
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            Assert.Equal(1, HealthProbe.Run(port, TimeSpan.FromSeconds(10)));
        }
    }
}
