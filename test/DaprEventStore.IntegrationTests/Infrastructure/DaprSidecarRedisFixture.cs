using System.Net.Http.Json;
using Testcontainers.Redis;

namespace DaprEventStore.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit collection using a Redis-backed pub/sub component.
/// Eliminates the in-memory pub/sub deadlock for multi-key outbox transactions,
/// enabling full AutoPublish mode validation.
/// </summary>
[CollectionDefinition("DaprSidecarRedis")]
public class DaprSidecarRedisCollection : ICollectionFixture<DaprSidecarRedisFixture> { }

/// <summary>
/// xUnit fixture that starts Redis (via Testcontainers), generates Dapr component YAMLs
/// pointing to it, then starts a daprd sidecar on separate ports from the in-memory fixture.
/// Requires Docker to be running.
/// </summary>
public sealed class DaprSidecarRedisFixture : IAsyncLifetime
{
    public const int DaprHttpPort = 3502;
    public const int DaprGrpcPort = 50003;
    public const int AppPort      = 5202;
    public const string AppId     = "integration-test-app-redis";

    private RedisContainer? _redis;
    private Process? _process;
    private TestAppServer? _appServer;
    private string? _componentsDir;

    public TestAppServer TestAppServer => _appServer!;

    public async Task InitializeAsync()
    {
        Console.WriteLine("[RedisFixture] Starting Redis Testcontainer...");
        _redis = new RedisBuilder("redis:7-alpine")
            .Build();
        await _redis.StartAsync();
        var redisHost = _redis.GetConnectionString(); // "localhost:PORT"
        Console.WriteLine($"[RedisFixture] Redis running at {redisHost}");

        _componentsDir = Path.Combine(Path.GetTempPath(), $"dapr-redis-components-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_componentsDir);
        WriteComponents(_componentsDir, redisHost);

        Console.WriteLine("[RedisFixture] Killing stale processes...");
        KillProcessOnPort(DaprHttpPort);
        KillProcessOnPort(9092);
        KillProcessOnPort(AppPort);

        Console.WriteLine("[RedisFixture] Starting TestAppServer...");
        _appServer = new TestAppServer(AppPort);
        await _appServer.StartAsync();

        var startInfo = new ProcessStartInfo
        {
            FileName = "daprd",
            Arguments = string.Join(" ",
                $"--app-id {AppId}",
                $"--app-port {AppPort}",
                $"--dapr-http-port {DaprHttpPort}",
                $"--dapr-grpc-port {DaprGrpcPort}",
                $"--resources-path \"{_componentsDir}\"",
                "--metrics-port 9092",
                "--log-level info"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _componentsDir
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start daprd. Ensure Dapr CLI is installed.");

        _ = DrainAsync(_process.StandardOutput, "[daprd-redis out]");
        _ = DrainAsync(_process.StandardError,  "[daprd-redis err]");

        Console.WriteLine("[RedisFixture] daprd started, waiting for health...");
        await WaitForSidecarHealthAsync();
        Console.WriteLine("[RedisFixture] Health check passed, waiting 2s for subscriptions...");
        await Task.Delay(2000);
        Console.WriteLine("[RedisFixture] InitializeAsync complete.");
    }

    private static void WriteComponents(string dir, string redisHost)
    {
        // Copy static component files from build output.
        var staticDir = Path.GetFullPath("components-redis");
        foreach (var file in Directory.EnumerateFiles(staticDir, "*.yaml"))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        // pubsub.yaml contains the dynamic Redis host, so it is written here.
        File.WriteAllText(Path.Combine(dir, "pubsub.yaml"),
            $"""
            apiVersion: dapr.io/v1alpha1
            kind: Component
            metadata:
              name: pubsub
            spec:
              type: pubsub.redis
              version: v1
              metadata:
                - name: redisHost
                  value: "{redisHost}"
            """);
    }

    private static async Task DrainAsync(System.IO.StreamReader reader, string prefix)
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

    private static async Task WaitForSidecarHealthAsync(int timeoutSeconds = 45)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var healthUrl = $"http://localhost:{DaprHttpPort}/v1.0/healthz/outbound";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch { }
            await Task.Delay(300);
        }

        throw new TimeoutException($"Dapr sidecar (Redis) did not become healthy within {timeoutSeconds}s.");
    }

    public async Task DisposeAsync()
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

        if (_appServer != null)
            await _appServer.DisposeAsync();

        if (_redis != null)
            await _redis.DisposeAsync();

        if (_componentsDir != null && Directory.Exists(_componentsDir))
        {
            try { Directory.Delete(_componentsDir, recursive: true); } catch { }
        }
    }
}
