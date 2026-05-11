using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Azure.Cosmos;

namespace DaprEventStore.IntegrationTests.Infrastructure;

[CollectionDefinition("DaprSidecarCosmos")]
public class DaprSidecarCosmosCollection : ICollectionFixture<DaprSidecarCosmosFixture> { }

/// <summary>
/// xUnit fixture that starts the Azure Cosmos DB v2 Linux emulator
/// (mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview-testcontainers)
/// in HTTP mode, then starts daprd with the state.azure.cosmosdb component.
///
/// The v2 emulator runs on HTTP on port 8081. Both daprd and the .NET CosmosClient connect
/// via http://127.0.0.1:{port} (explicit IPv4 to avoid localhost → ::1 resolution on Windows).
/// The .NET Cosmos SDK requires HTTPS URIs, so CreateCosmosClient() uses an HttpClientHandler
/// that rewrites every request from https:// to http:// before it hits the wire.
/// </summary>
public sealed class DaprSidecarCosmosFixture : IAsyncLifetime
{
    public const int    DaprHttpPort  = 3503;
    public const int    DaprGrpcPort  = 50004;
    public const int    AppPort       = 5203;
    public const string AppId         = "integration-test-app-cosmos";
    public const string StoreName     = "statestore";
    public const string DatabaseName  = "dapr";
    public const string ContainerName = "state";

    // Well-known emulator master key (same across all emulator versions).
    public const string MasterKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private IContainer?    _cosmos;
    private Process?       _process;
    private TestAppServer? _appServer;

    public TestAppServer TestAppServer => _appServer!;
    public string        CosmosEndpoint { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Console.WriteLine("[CosmosFixture] Starting Cosmos DB v2 emulator (vnext-preview-testcontainers, HTTP)...");

        // The Azure SDK for Go (used by daprd's state.azure.cosmosdb component) normalises the
        // Cosmos endpoint to port 8081 when connecting over HTTP — it ignores any other port in
        // the URL.  Therefore we bind the container's port 8081 to EXACTLY port 8081 on the host.
        // Any process already listening on 8081 is killed first.
        KillProcessOnPort(8081);

        // v2 image: HTTP by default, no --privileged required.
        // Wait strategy: "Started" is logged by the emulator when it is ready.
        _cosmos = new ContainerBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview-testcontainers")
            .WithPortBinding(8081, 8081)
            .WithEnvironment("ENABLE_EXPLORER", "false")
            .WithEnvironment("ENABLE_TELEMETRY", "false")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Started"))
            .Build();

        await _cosmos.StartAsync();

        // Always port 8081 — fixed binding.
        CosmosEndpoint = "http://127.0.0.1:8081";
        Console.WriteLine($"[CosmosFixture] Emulator ready at {CosmosEndpoint}");

        // Dapr's state.azure.cosmosdb component does NOT create the database/container
        // automatically — pre-create them now.  Partition key must be /partitionKey (Dapr requirement).
        Console.WriteLine("[CosmosFixture] Pre-creating Cosmos database and container...");
        await CreateDatabaseAndContainerAsync();
        Console.WriteLine("[CosmosFixture] Database and container ready.");

        KillProcessOnPort(DaprHttpPort);
        KillProcessOnPort(9093);
        KillProcessOnPort(AppPort);

        _appServer = new TestAppServer(AppPort);
        await _appServer.StartAsync();

        _process = Process.Start(new ProcessStartInfo
        {
            FileName               = "daprd",
            Arguments              = string.Join(" ",
                $"--app-id {AppId}",
                $"--app-port {AppPort}",
                $"--dapr-http-port {DaprHttpPort}",
                $"--dapr-grpc-port {DaprGrpcPort}",
                $"--resources-path \"{Path.GetFullPath("components-cosmos")}\"",
                "--metrics-port 9093",
                "--log-level info"),
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        }) ?? throw new InvalidOperationException("Failed to start daprd.");

        _ = DrainAsync(_process.StandardOutput, "[daprd-cosmos out]");
        _ = DrainAsync(_process.StandardError,  "[daprd-cosmos err]");

        Console.WriteLine("[CosmosFixture] daprd started, waiting for health...");
        await WaitForSidecarHealthAsync();
        Console.WriteLine("[CosmosFixture] Health check passed, waiting 2s...");
        await Task.Delay(2000);
        Console.WriteLine("[CosmosFixture] InitializeAsync complete.");
    }

    /// <summary>
    /// Returns a <see cref="CosmosClient"/> suitable for direct document inspection in tests.
    /// Skips TLS validation because the emulator uses a self-signed certificate.
    /// </summary>
    /// <summary>
    /// Returns a <see cref="CosmosClient"/> suitable for direct document inspection in tests.
    /// The .NET Cosmos SDK requires an https:// URI, so we rewrite each request to http://
    /// before it leaves the process — the emulator is running in plain HTTP mode.
    /// </summary>
    public CosmosClient CreateCosmosClient()
    {
        return new CosmosClient(
            // SDK requires https:// in the URI even though the wire protocol is http://
            "https://127.0.0.1:8081",
            MasterKey,
            new CosmosClientOptions
            {
                ConnectionMode    = ConnectionMode.Gateway,
                HttpClientFactory = () => new HttpClient(new HttpsToHttpHandler(8081))
            });
    }

    private async Task CreateDatabaseAndContainerAsync()
    {
        using var client = CreateCosmosClient();

        // The v2 emulator logs "Started" before the internal PostgreSQL schema is fully
        // initialised.  Retry until CreateDatabaseIfNotExistsAsync succeeds (or 60 s elapses).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                var dbResponse = await client.CreateDatabaseIfNotExistsAsync(DatabaseName);
                await dbResponse.Database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties(ContainerName, "/partitionKey"));
                return;
            }
            catch (CosmosException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
            }
        }
    }

    // -- Private helpers ------------------------------------------------------

    /// <summary>
    /// Rewrites the URI scheme from https to http so the .NET Cosmos SDK can talk
    /// to the emulator running in plain HTTP mode.
    /// </summary>
    private sealed class HttpsToHttpHandler : HttpClientHandler
    {
        private readonly int _port;
        public HttpsToHttpHandler(int port) => _port = port;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Scheme == "https")
                request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "http", Port = _port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
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
                UseShellExecute        = false,
                CreateNoWindow         = true
            })!;
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port} ") || !line.Contains("LISTENING")) continue;
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                    try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { }
    }

    private static async Task WaitForSidecarHealthAsync(int timeoutSeconds = 120)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var healthUrl = $"http://localhost:{DaprHttpPort}/v1.0/healthz/outbound";
        var deadline  = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var r = await client.GetAsync(healthUrl);
                if (r.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(300);
        }

        throw new TimeoutException($"Dapr sidecar (Cosmos) did not become healthy within {timeoutSeconds}s.");
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
            try { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); } catch { }
        _process?.Dispose();

        if (_appServer != null)
            await _appServer.DisposeAsync();

        if (_cosmos != null)
            await _cosmos.DisposeAsync();
    }
}