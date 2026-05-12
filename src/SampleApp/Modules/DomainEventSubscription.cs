using System.Text.Json;
using DaprEventStore;

namespace SampleApp.Modules;

public static class DomainEventSubscription
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Registers a project-level endpoint that receives VersionedEvents wrapped in a CloudEvent
    /// envelope (matching Dapr's outbox AutoPublish format), resolves all registered IModule
    /// instances, and dispatches each domain event to every module's When() handler.
    /// </summary>
    public static RouteHandlerBuilder MapDomainEvents(this IEndpointRouteBuilder app)
        => app.MapPost("domain-events", Handle)
              .WithTopic("pubsub", "domain-events");

    private static async Task<IResult> Handle(HttpRequest req, IEnumerable<IModule> modules) // TODO resolve json options + cache reflection
    {
        using var doc = await JsonDocument.ParseAsync(req.Body);

        if (!doc.RootElement.TryGetProperty("data", out var versionedEventElement))
            return Results.Ok();

        var versionedEvent = versionedEventElement.Deserialize<VersionedEvent>(JsonOptions);

        if (versionedEvent is null || versionedEvent.Data is not JsonElement dataElement)
            return Results.Ok();

        var eventType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.Name == versionedEvent.EventName && t.IsAssignableTo(typeof(Event)));

        if (eventType is null)
            return Results.Ok();

        var domainEvent = (Event?)dataElement.Deserialize(eventType, JsonOptions);

        if (domainEvent is null)
            return Results.Ok();

        foreach (var module in modules)
            await module.When(domainEvent);

        return Results.Ok();
    }
}
