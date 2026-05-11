using System.Text.Json;
using Dapr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DaprEventStore;

/// <summary>
/// Invokes an ASP.NET endpoint in-process via <see cref="RequestDelegate"/> —
/// no HttpClient, no TCP. Mirrors Dapr's per-event pub/sub delivery by
/// POSTing each event as JSON directly through the endpoint pipeline.
/// </summary>
public sealed class InProcessEndpointPublisher
{
    private readonly RequestDelegate requestDelegate;
    private readonly IServiceProvider serviceProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <param name="requestDelegate">The endpoint delegate to invoke for each event.</param>
    /// <param name="serviceProvider">Service provider for <see cref="DefaultHttpContext.RequestServices"/>.</param>
    public InProcessEndpointPublisher(RequestDelegate requestDelegate, IServiceProvider serviceProvider)
    {
        this.requestDelegate = requestDelegate ?? throw new ArgumentNullException(nameof(requestDelegate));
        this.serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Finds the endpoint registered with Dapr's <see cref="TopicAttribute"/> metadata,
    /// matching by pubsub name and topic — the same approach <c>MapSubscribeHandler</c> uses.
    /// </summary>
    public static InProcessEndpointPublisher ForTopic(IServiceProvider serviceProvider, string pubsubName, string topicName)
    {
        var endpoint = serviceProvider.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .FirstOrDefault(e =>
            {
                var topic = e.Metadata.GetMetadata<ITopicMetadata>();
                return topic is not null
                    && topic.PubsubName == pubsubName
                    && topic.Name == topicName;
            })
            ?? throw new InvalidOperationException(
                $"No endpoint found with [Topic(\"{pubsubName}\", \"{topicName}\")]. " +
                "Ensure the endpoint is registered with TopicAttribute metadata.");

        return new InProcessEndpointPublisher(
            endpoint.RequestDelegate ?? throw new InvalidOperationException("Matched endpoint has no RequestDelegate."),
            serviceProvider);
    }

    /// <summary>
    /// Finds the endpoint by its route pattern string (e.g. "events").
    /// </summary>
    public static InProcessEndpointPublisher ForRoute(IServiceProvider serviceProvider, string routePattern)
    {
        var endpoint = serviceProvider.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(e => e.RoutePattern.RawText == routePattern)
            ?? throw new InvalidOperationException(
                $"No endpoint found with route pattern '{routePattern}'.");

        return new InProcessEndpointPublisher(
            endpoint.RequestDelegate ?? throw new InvalidOperationException("Matched endpoint has no RequestDelegate."),
            serviceProvider);
    }

    /// <summary>
    /// Publishes each event individually by invoking the endpoint's RequestDelegate.
    /// Each event is wrapped in a CloudEvent envelope matching Dapr's outbox AutoPublish format.
    /// Matches the <c>Func&lt;VersionedEvent[], Task&gt;</c> signature expected by <see cref="InMemoryEventStore"/>.
    /// </summary>
    public async Task PublishAsync(VersionedEvent[] events)
    {
        foreach (var evt in events)
        {
            try
            {
                // Wrap in CloudEvent to match Dapr's outbox delivery format
                var dataElement = JsonSerializer.SerializeToElement(evt, JsonOptions);
                var cloudEvent = new CloudEvent<JsonElement>(dataElement)
                {
                    Type = "com.dapr.event.sent",
                    Source = new Uri("urn:inprocess"),
                };

                var context = new DefaultHttpContext { RequestServices = serviceProvider };
                context.Request.Method = HttpMethods.Post;
                context.Request.ContentType = "application/json";
                context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(cloudEvent, JsonOptions));
                context.Response.Body = new MemoryStream();

                await requestDelegate(context);

                if (context.Response.StatusCode >= 400)
                {
                    context.Response.Body.Seek(0, SeekOrigin.Begin);
                    using var reader = new StreamReader(context.Response.Body);
                    var body = await reader.ReadToEndAsync();
                    Console.WriteLine($"[InProcessPublisher] Endpoint returned {context.Response.StatusCode} for event '{evt.EventId}': {body}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InProcessPublisher] Failed to invoke endpoint for event '{evt.EventId}': {ex.Message}");
            }
        }
    }
}
