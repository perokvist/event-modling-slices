using System.Threading.Channels;
using Dapr.Client;

namespace SampleApp.Modules.TodoList;

public class TodoAutomationOptions
{
    public string TopicName { get; set; } = "todo-work";
    public string PubSubName { get; set; } = "pubsub";
    public bool PublishToPubSub { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
}

public interface ITodoDispatcher
{
    Task DispatchAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken);
}

public class HybridTodoDispatcher(
    Channel<TodoItem<TodoPayload>> channel,
    DaprClient dapr,
    TodoAutomationOptions options,
    ILogger<HybridTodoDispatcher> logger) : ITodoDispatcher
{
    public async Task DispatchAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync(todo, cancellationToken);

        if (!options.PublishToPubSub)
            return;

        var meta = new Dictionary<string, string> { { "contentType", "application/json" } };
        try
        {
            await dapr.PublishEventAsync(options.PubSubName, options.TopicName, todo, metadata: meta, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish todo {TodoId} to pub/sub topic {Topic}.", todo.Id, options.TopicName);
        }
    }
}

public interface ITodoEffectExecutor
{
    Task ExecuteAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken);
}

public class LoggingTodoEffectExecutor(ILogger<LoggingTodoEffectExecutor> logger) : ITodoEffectExecutor
{
    public Task ExecuteAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken)
    {
        logger.LogInformation("Executing todo {TodoId} from {EventName}", todo.Id, todo.Payload.EventName);
        return Task.CompletedTask;
    }
}

