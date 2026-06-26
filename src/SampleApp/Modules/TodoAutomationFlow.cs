using DaprEventStore;

namespace SampleApp.Modules;

public static class TodoAutomationFlow
{
    public static async Task<TState> ApplyAsync<TState, TTrigger>(
        IStateStore stateStore,
        string stateId,
        Projection<Event, TState> projection,
        Event @event,
        Func<TState, TTrigger, Command?> execute,
        Func<Command, Task<Result>> dispatch,
        CancellationToken cancellationToken = default)
        where TTrigger : Event
    {
        var state = await stateStore.GetAsync<TState>(stateId, cancellationToken) ?? projection.InitialState;
        var evolved = projection.Evolve(state, @event);

        if (!EqualityComparer<TState>.Default.Equals(state, evolved))
            await stateStore.SaveAsync(stateId, evolved, cancellationToken);

        if (@event is TTrigger trigger)
        {
            var command = execute(evolved, trigger);
            if (command is not null)
                await dispatch(command);
        }

        return evolved;
    }

    public static async Task<TState> ApplyFromStreamAsync<TState, TTrigger>(
        IEventStore eventStore,
        string streamName,
        Projection<Event, TState> projection,
        Event @event,
        Func<TState, TTrigger, Command?> execute,
        Func<Command, Task<Result>> dispatch,
        CancellationToken cancellationToken = default)
        where TTrigger : Event
    {
        var state = projection.InitialState;
        await foreach (var historical in eventStore.LoadEventStreamAsync<Event>(streamName, 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = projection.Evolve(state, historical);
        }

        var evolved = projection.Evolve(state, @event);

        if (@event is TTrigger trigger)
        {
            var command = execute(evolved, trigger);
            if (command is not null)
                await dispatch(command);
        }

        return evolved;
    }
}
