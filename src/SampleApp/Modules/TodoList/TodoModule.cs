using System.Text.Json;
using System.Threading.Channels;
using DaprEventStore;

namespace SampleApp.Modules.TodoList;

public class TodoModule(IEventStore store, ITodoDispatcher dispatcher) : IModule
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public Task<Result> Dispatch(Command command)
        => Task.FromResult(Result.Failure(new NotSupportedException("TodoModule does not handle commands.")));

    public async Task When(Event @event)
    {
        if (!ShouldQueue(@event))
            return;

        var todoId = TodoIds.CreateDeterministic(nameof(TodoModule), @event.EventId);
        var payload = new TodoPayload(@event.GetType().Name, @event.EventId);
        var queued = new TodoQueued(Guid.NewGuid(), todoId, payload);

        await AppendAsync(EventData.Create(nameof(TodoQueued), queued));
        await dispatcher.DispatchAsync(new TodoItem<TodoPayload>(todoId, payload, TodoStatus.Pending), CancellationToken.None);
    }

    public async ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
    {
        if (query is not GetTodoListQuery)
            return default;

        var events = await LoadTodoEvents();
        var state = events.Aggregate(TodoProjection.TodoList.InitialState, TodoProjection.TodoList.Evolve);

        return (T?)(object)new TodoListReadModel(state);
    }

    private static bool ShouldQueue(Event @event)
        => @event is DomainEvent
           and not TodoQueued
           and not TodoCompleted
           and not TodoFailed;

    private async Task AppendAsync(params EventData[] events)
    {
        var meta = await store.GetStreamMetaData(TodoProcessorBackgroundService.TodoStream);
        var version = meta?.Version ?? 0;
        await store.AppendToStreamAsync(TodoProcessorBackgroundService.TodoStream, version, events);
    }

    private async Task<IReadOnlyList<Event>> LoadTodoEvents()
    {
        var result = new List<Event>();
        await foreach (var versioned in store.LoadEventStreamAsync(TodoProcessorBackgroundService.TodoStream, 0))
        {
            var parsed = TryParse(versioned);
            if (parsed is not null)
                result.Add(parsed);
        }
        return result;
    }

    private static Event? TryParse(VersionedEvent versioned)
    {
        if (versioned.Data is not JsonElement json)
            return null;

        return versioned.EventName switch
        {
            nameof(TodoQueued) => json.Deserialize<TodoQueued>(JsonOptions),
            nameof(TodoCompleted) => json.Deserialize<TodoCompleted>(JsonOptions),
            nameof(TodoFailed) => json.Deserialize<TodoFailed>(JsonOptions),
            _ => null
        };
    }
}

public static class TodoModuleExtensions
{
    public static IServiceCollection AddTodoAutomation(this IServiceCollection services, Action<TodoAutomationOptions>? configure = null)
    {
        var options = new TodoAutomationOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(Channel.CreateUnbounded<TodoItem<TodoPayload>>());
        services.AddSingleton<ITodoDispatcher, HybridTodoDispatcher>();
        services.AddSingleton<ITodoEffectExecutor, LoggingTodoEffectExecutor>();
        services.AddHostedService<TodoProcessorBackgroundService>();
        services.AddModule<TodoModule>();

        return services;
    }
}

