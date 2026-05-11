using System.Net.Http.Json;

namespace DaprEventStore.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit fixture that starts a daprd sidecar process for integration tests.
/// Requires the Dapr CLI to be installed (dapr init must have been run).
/// </summary>
public sealed class DaprSidecarFixture : IAsyncLifetime
{
    public const int DaprHttpPort = 3501;
    public const int DaprGrpcPort = 50002;
    public const string AppId = "integration-test-app";
    public const string ComponentsPath = "components";

    private Process? _process;
    private TestAppServer? _appServer;

    public TestAppServer TestAppServer => _appServer!;

    public async Task InitializeAsync()
    {
        Console.WriteLine("[Fixture] Killing stale processes...");
        KillProcessOnPort(DaprHttpPort);
        KillProcessOnPort(9091);
        KillProcessOnPort(TestAppServer.DefaultAppPort);

        Console.WriteLine("[Fixture] Starting TestAppServer...");
        _appServer = new TestAppServer();
        await _appServer.StartAsync();
        Console.WriteLine("[Fixture] TestAppServer started.");

        var componentsPath = Path.GetFullPath(ComponentsPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "daprd",
            Arguments = string.Join(" ",
                $"--app-id {AppId}",
                $"--app-port {_appServer.Port}",
                $"--dapr-http-port {DaprHttpPort}",
                $"--dapr-grpc-port {DaprGrpcPort}",
                $"--resources-path \"{componentsPath}\"",
                "--metrics-port 9091",
                "--log-level info"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(componentsPath)
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start daprd process. Ensure Dapr CLI is installed and 'dapr init' has been run.");

        // Drain stdout/stderr asynchronously to prevent buffer-fill deadlock
        _ = DrainAsync(_process.StandardOutput, "[daprd out]");
        _ = DrainAsync(_process.StandardError,  "[daprd err]");

        Console.WriteLine("[Fixture] daprd started, waiting for health...");
        await WaitForSidecarHealthAsync();
        Console.WriteLine("[Fixture] Health check passed, waiting 2s for subscriptions...");
        // Give daprd time to load subscriptions after outbound health is ready
        await Task.Delay(2000);
        Console.WriteLine("[Fixture] InitializeAsync complete.");
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

    private static async Task WaitForSidecarHealthAsync(int timeoutSeconds = 30)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        // Use /outbound endpoint: resolves when components are ready WITHOUT waiting for the
        // app channel. The plain /healthz blocks until /dapr/subscribe succeeds, causing a deadlock.
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
            catch
            {
                // not ready yet
            }
            await Task.Delay(200);
        }

        throw new TimeoutException($"Dapr sidecar did not become healthy within {timeoutSeconds} seconds.");
    }

    public async Task DisposeAsync()
    {
        // Kill daprd first so it stops calling the app server
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
    }
}
