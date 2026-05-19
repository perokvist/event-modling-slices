using System.Threading.Channels;
using DaprEventStore;

namespace SampleApp.Modules.TodoList;

public class TodoProcessorBackgroundService(
    Channel<TodoItem<TodoPayload>> queue,
    ITodoEffectExecutor executor,
    IServiceScopeFactory scopeFactory,
    TodoAutomationOptions options,
    ILogger<TodoProcessorBackgroundService> logger) : BackgroundService
{
    public const string TodoStream = "todo-list";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var todo = await queue.Reader.ReadAsync(stoppingToken);
            await ProcessAsync(todo, stoppingToken);
        }
    }

    public async Task ProcessAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken)
    {
        try
        {
            await executor.ExecuteAsync(todo, cancellationToken);
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.AppendToStreamAsync(TodoStream, EventData.Create(nameof(TodoCompleted),
                new TodoCompleted(Guid.NewGuid(), todo.Id)));
        }
        catch (Exception ex)
        {
            var attempts = todo.Attempts + 1;
            if (attempts < options.MaxAttempts)
            {
                await queue.Writer.WriteAsync(todo with { Attempts = attempts }, cancellationToken);
                logger.LogWarning(ex, "Todo {TodoId} failed attempt {Attempt}. Requeued.", todo.Id, attempts);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
            await store.AppendToStreamAsync(TodoStream, EventData.Create(nameof(TodoFailed),
                new TodoFailed(Guid.NewGuid(), todo.Id, ex.Message, attempts)));
        }
    }
}

