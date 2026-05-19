using System.Diagnostics;
using Dapr.Client;
using DaprEventStore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SampleApp.Modules;

namespace SampleApp.Tests;

/// <summary>
/// Extends <see cref="Fixture"/> to run API tests against a real Dapr sidecar.
///
/// Architecture:
/// - The base <see cref="Fixture"/> TestServer handles command processing via DaprEventStore
///   pointed at the real sidecar (HTTP 3502 / gRPC 50003).
/// - A separate minimal Kestrel host on port 5202 provides the subscription endpoint
///   (POST /domain-events) that daprd delivers outbox pub/sub messages to.
///   It shares the same TestModule instance from the base fixture so the observable
///   works unchanged.
///
/// All test utilities (WithHistory, Test, WithLogging, TestModule observable) are
/// inherited from <see cref="Fixture"/> with no duplication.
///
/// Requires daprd to be installed (dapr init must have been run).
/// Mark tests with [Trait("Category", "Dapr")] to allow CI filtering.
/// </summary>
public class DaprFixture : Fixture, IAsyncLifetime
{
    private const int DeliveryPort = 5202;
    private const int DaprHttpPort = 3502;
    private const int DaprGrpcPort = 50003;
    private const int MetricsPort = 9092;
    private const string AppId = "sampleapp-test";
    private const string ComponentsPath = "components";

    private WebApplication? _deliveryApp;
    private Process? _process;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder); // sets up TestModule, logging, InMemoryEventStore

        builder.ConfigureTestServices(services =>
        {
            // Replace InMemoryEventStore with DaprEventStore
            var storeDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEventStore));
            if (storeDescriptor != null) services.Remove(storeDescriptor);

            // Replace DaprClient to point at our sidecar
            var daprDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DaprClient));
            if (daprDescriptor != null) services.Remove(daprDescriptor);

            services.AddDaprClient(b => b
                .UseHttpEndpoint($"http://localhost:{DaprHttpPort}")
                .UseGrpcEndpoint($"http://localhost:{DaprGrpcPort}"));

            services.AddScoped<IEventStore>(sp =>
            {
                var dapr = sp.GetRequiredService<DaprClient>();
                return new DaprEventStore.DaprEventStore(dapr)
                {
                    StoreName = "statestore",
                    OutboxTopic = "domain-events"
                }.PartitionAllStream();
            });
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Trigger base TestServer host creation so DI is ready before starting daprd
        _ = Services;

        // Start minimal Kestrel app for daprd pub/sub delivery.
        // Shares the TestModule instance from base so the observable captures sidecar-delivered events.
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseUrls($"http://localhost:{DeliveryPort}");
        appBuilder.Services.AddSingleton<IModule>(Module);
        _deliveryApp = appBuilder.Build();
        _deliveryApp.MapSubscribeHandler();
        _deliveryApp.MapDomainEvents();
        await _deliveryApp.StartAsync();

        KillProcessOnPort(DaprHttpPort);
        KillProcessOnPort(MetricsPort);

        var componentsPath = Path.GetFullPath(ComponentsPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "daprd",
            Arguments = string.Join(" ",
                $"--app-id {AppId}",
                $"--app-port {DeliveryPort}",
                $"--dapr-http-port {DaprHttpPort}",
                $"--dapr-grpc-port {DaprGrpcPort}",
                $"--resources-path \"{componentsPath}\"",
                $"--metrics-port {MetricsPort}",
                "--log-level info"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Failed to start daprd. Ensure Dapr CLI is installed and 'dapr init' has been run.");

        _ = DrainAsync(_process.StandardOutput, "[daprd out]");
        _ = DrainAsync(_process.StandardError, "[daprd err]");

        await WaitForHealthAsync();
        await Task.Delay(2000); // allow sidecar to load subscriptions from delivery app
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch { }
        }
        _process?.Dispose();

        if (_deliveryApp != null)
            await _deliveryApp.StopAsync();
    }

    private static async Task WaitForHealthAsync(int timeoutSeconds = 30)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var url = $"http://localhost:{DaprHttpPort}/v1.0/healthz/outbound";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(200);
        }

        throw new TimeoutException($"Dapr sidecar did not become healthy within {timeoutSeconds}s.");
    }

    private static async Task DrainAsync(StreamReader reader, string prefix)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
                Console.WriteLine($"{prefix} {line}");
        }
        catch { }
    }

    private static void KillProcessOnPort(int port)
    {
        try
        {
            var netstat = Process.Start(new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port} ") || !line.Contains("LISTENING")) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                {
                    try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }
    }
}
