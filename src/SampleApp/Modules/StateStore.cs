using System.Collections.Concurrent;

namespace SampleApp.Modules;

public interface IStateStore
{
    ValueTask<TState?> GetAsync<TState>(string id, CancellationToken cancellationToken = default);
    ValueTask SaveAsync<TState>(string id, TState state, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStateStore : IStateStore
{
    private readonly ConcurrentDictionary<string, object?> values = new();

    public ValueTask<TState?> GetAsync<TState>(string id, CancellationToken cancellationToken = default)
    {
        if (!values.TryGetValue(id, out var state))
            return ValueTask.FromResult<TState?>(default);
        if (state is TState typed)
            return ValueTask.FromResult<TState?>(typed);

        throw new InvalidOperationException($"State value with id '{id}' is not of expected type '{typeof(TState).Name}'.");
    }

    public ValueTask SaveAsync<TState>(string id, TState state, CancellationToken cancellationToken = default)
    {
        values[id] = state;
        return ValueTask.CompletedTask;
    }
}
