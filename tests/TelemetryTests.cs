using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using cartservice;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace cartservice.tests
{
    public sealed class TelemetryTests
    {
        private static readonly ServiceIdentity Id = new("cart-service", "1.2.3", "abc1234", "staging", "pod-9");

        [Fact]
        public void Resource_HasTheStandardAttributes()
        {
            Dictionary<string, object> attrs = ResourceBuilder.CreateEmpty().AddIdentity(Id).Build().Attributes
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            Assert.Equal("cart-service", attrs["service.name"]);
            Assert.Equal("1.2.3", attrs["service.version"]);
            Assert.Equal("everly", attrs["service.namespace"]);
            Assert.Equal("staging", attrs["deployment.environment"]);
            Assert.Equal("pod-9", attrs["service.instance.id"]);
        }

        [Fact]
        public void BuildInfo_IsAGaugeOfOneWithServiceVersionCommit()
        {
            ServiceCollection services = new();
            _ = services.AddEverlyTelemetry(Id, new Uri("http://127.0.0.1:1"), 60000);

            List<(double Value, Dictionary<string, string?> Tags)> seen = [];
            using MeterListener listener = new();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TelemetryExtensions.BuildInfoMeter && instrument.Name == "build_info")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
                seen.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString()))));
            listener.Start();
            listener.RecordObservableInstruments();

            (double value, Dictionary<string, string?> tagMap) = Assert.Single(seen);
            Assert.Equal(1, value);
            Assert.Equal(new Dictionary<string, string?> { ["service"] = "cart-service", ["version"] = "1.2.3", ["commit"] = "abc1234" }, tagMap);
        }

        [Fact]
        public void ExportersAreBounded()
        {
            // every export has a deadline and the host drains for a short, fixed time: the sum stays below the 10 s SIGTERM budget
            Assert.True((3 * TelemetryExtensions.ExportTimeoutMs / 1000.0) + TelemetryExtensions.ShutdownTimeout.TotalSeconds < 10);
        }

        [Fact]
        public async Task BlackholedCollector_NeverBlocksStartupOrShutdown()
        {
            // A collector that accepts TCP connections and never answers: what a firewall that drops replies looks like.
            using TcpListener blackhole = new(IPAddress.Loopback, 0);
            blackhole.Start();
            List<TcpClient> held = [];
            using CancellationTokenSource stopAccepting = new();
            Task acceptor = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        held.Add(await blackhole.AcceptTcpClientAsync(stopAccepting.Token));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            int port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            _ = builder.Services.AddEverlyTelemetry(Id, new Uri($"http://127.0.0.1:{port}"), 500);
            _ = builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("blackhole.test"));
            _ = builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TelemetryExtensions.ShutdownTimeout);
            IHost host = builder.Build();

            // monotonic clock (Stopwatch) and a generous deadline: the host wall clock jumps
            Stopwatch startup = Stopwatch.StartNew();
            await host.StartAsync();
            Assert.True(startup.Elapsed < TimeSpan.FromSeconds(5), $"startup took {startup.Elapsed}");

            using (ActivitySource source = new("blackhole.test"))
            {
                for (int i = 0; i < 20; i++)
                {
                    using Activity? a = source.StartActivity("pending");
                }
            }

            await Task.Delay(1500); // let the periodic metric reader try an export too

            Stopwatch shutdown = Stopwatch.StartNew();
            await host.StopAsync();
            host.Dispose();
            shutdown.Stop();

            await stopAccepting.CancelAsync();
            await acceptor;
            blackhole.Stop();
            foreach (TcpClient c in held)
            {
                c.Dispose();
            }

            Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(9), $"shutdown took {shutdown.Elapsed}");
        }
    }
}
