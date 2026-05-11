using Dapr.Client;
using IntegrationPublisher = System.Func<SampleApp.Modules.Event[], System.Threading.Tasks.Task>;

namespace SampleApp.Modules;
public static class Subscriptions
{
    public static IServiceCollection IntegrationPublisher(this IServiceCollection services, string topicName, string pubSubName)
        => services.AddSingleton<IntegrationPublisher>(sp => async events =>
        {
            var meta = new Dictionary<string, string>
            {
                { "contentType", "application/json" }
            };

            var dapr = sp.GetRequiredService<DaprClient>();
            await dapr.PublishEventAsync(pubSubName, topicName, events, metadata: meta);
        });

    //public static RouteHandlerBuilder Inbox(this RouteGroupBuilder builder, string name, string pubSubName)
    //    => Subscription(builder, name)
    //    .WithTopic(pubSubName, name)
    //    .WithOpenApi();
        
    //public static void Outbox(this RouteGroupBuilder builder, string name, string pubSubName)
    //    => Subscription(builder, name)
    //    .WithTopic(pubSubName, name)
    //    .WithOpenApi();

    //private static RouteHandlerBuilder Subscription(RouteGroupBuilder builder, string name)
    // => builder.MapPost(name, async (Event[] events, TestModule module, ILoggerFactory logger) =>
    // {
    //     var l = logger.CreateLogger(name);
    //     l.LogInformation($"Subscription - {name} - recived : {events.GetType()}");

    //     foreach (var e in events)
    //     {
    //         l.LogInformation("Handling {0}", e.GetType().Name);
    //         await module.When(e);
    //     }

    //     return Results.Ok();
    // });

}
