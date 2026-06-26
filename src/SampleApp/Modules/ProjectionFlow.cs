namespace SampleApp.Modules;

public static class ProjectionFlow
{
    public static TState? Apply<TKey, TState, TEvent>(
        IDictionary<TKey, TState> store,
        TKey key,
        TEvent @event,
        Func<TState?, TEvent, TState?> evolve)
        where TKey : notnull
    {
        var hasCurrent = store.TryGetValue(key, out var current);
        var next = evolve(hasCurrent ? current : default, @event);

        if (next is null)
        {
            if (hasCurrent)
                store.Remove(key);

            return default;
        }

        if (!hasCurrent || !EqualityComparer<TState>.Default.Equals(current, next))
            store[key] = next;

        return next;
    }
}
