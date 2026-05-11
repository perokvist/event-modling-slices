using System.Text.Json;
using System.Threading.Channels;
using Dapr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DaprEventStore.IntegrationTests.Infrastructure;

/// <summary>
/// Extension methods for <see cref="CloudEvent{TData}"/> with <see cref="JsonElement"/> data,
/// providing test-friendly helpers for inspecting pub/sub message payloads.
/// </summary>
public static class CloudEventExtensions
{
    /// <summary>Returns true if the serialized Data payload contains the specified text.</summary>
    public static bool DataContains(this CloudEvent<JsonElement> ce, string text)
        => ce.Data.ValueKind != JsonValueKind.Undefined && ce.Data.GetRawText().Contains(text);
}

/// <summary>
/// Minimal Kestrel-based HTTP server acting as the Dapr "app" for integration tests.
/// Handles /dapr/subscribe discovery and captures pub/sub deliveries into a channel.
/// Uses Kestrel (not HttpListener/http.sys) to avoid Windows http.sys 400 quirks.
/// </summary>
public sealed class TestAppServer : IAsyncDisposable
{
    public const int DefaultAppPort = 5201;
    public const string Topic = "events";
    public const string PubSubName = "pubsub";

    private static readonly byte[] EmptyJson = System.Text.Encoding.UTF8.GetBytes("[]");

    private readonly Channel<CloudEvent<JsonElement>> messages = Channel.CreateUnbounded<CloudEvent<JsonElement>>();
    private WebApplication? app;

    public int Port { get; }

    public TestAppServer(int port = DefaultAppPort)
    {
        Port = port;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel()
            .UseUrls($"http://localhost:{Port}");
        builder.Logging.ClearProviders(); // suppress Kestrel noise in test output

        app = builder.Build();

        // Dapr programmatic subscription discovery (replaces declarative subscription.yaml)
        app.MapSubscribeHandler();

        // Dapr health probe
        app.MapGet("/healthz", () => Results.Ok());

        // Dapr delivers outbox pub/sub messages here as CloudEvent JSON
        app.MapPost($"/{Topic}", async (HttpRequest req) =>
        {
            using var reader = new System.IO.StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            Console.WriteLine($"[TestAppServer] POST /{Topic}: {body[..Math.Min(80, body.Length)]}");
            var cloudEvent = JsonSerializer.Deserialize<CloudEvent<JsonElement>>(body)!;
            await messages.Writer.WriteAsync(cloudEvent);
            return Results.Ok();
        }).WithMetadata(new TopicAttribute(PubSubName, Topic));

        await app.StartAsync();
        Console.WriteLine($"[TestAppServer] Kestrel started on port {Port}");
    }

    /// <summary>Waits for the next pub/sub message delivery with the given timeout.</summary>
    public async Task<CloudEvent<JsonElement>> WaitForMessageAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await messages.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// Reads messages until one matches the predicate, or the timeout expires.
    /// Useful when a single transaction publishes multiple entries (e.g. stream head + events).
    /// </summary>
    public async Task<CloudEvent<JsonElement>> WaitForMatchingMessageAsync(Func<CloudEvent<JsonElement>, bool> predicate, TimeSpan timeout)
    {
        const int attempts = 3;
        var allSeen = new List<CloudEvent<JsonElement>>();
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            Console.WriteLine($"[TestAppServer] Waiting for matching message (attempt {attempt}/{attempts}) with timeout {timeout}");
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var message = await messages.Reader.ReadAsync(cts.Token);
                    var dataPreview = message.Data.ValueKind != JsonValueKind.Undefined
                        ? message.Data.GetRawText() : "";
                    Console.WriteLine($"[TestAppServer] Received message (data={dataPreview[..Math.Min(80, dataPreview.Length)]})");
                    allSeen.Add(message);
                    if (predicate(message))
                        return message;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[TestAppServer] Attempt {attempt} timed out after {timeout}. Messages seen so far: {allSeen.Count}");
                // small backoff before retrying
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        var recent = allSeen.TakeLast(5).Select(m =>
        {
            var raw = JsonSerializer.Serialize(m);
            return raw.Length > 200 ? raw[..200] + "..." : raw;
        });
        var sample = string.Join("\n----\n", recent);
        throw new TimeoutException($"WaitForMatchingMessageAsync timed out after {attempts} attempts. Total messages seen: {allSeen.Count}. Recent:\n{sample}");
    }

    public async ValueTask DisposeAsync()
    {
        if (app != null)
            await app.StopAsync();
    }
}