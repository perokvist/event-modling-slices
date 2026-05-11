using Microsoft.Extensions.Caching.Hybrid;

namespace SampleApp.Modules;

public static class ModuleExtensions
{
    extension(IModule module)
    {
        public async Task<IResult> Dispatch(Command command,
           Func<IResult> onSuccess)
        {
            var r = await module.Dispatch(command);
            return r.ToHttpResult(onSuccess);
        }

        public async Task<TResult?> Query<TModule, TResult>(
            Query<TResult> query,
            HybridCache cache,
            string key)
             where TModule : IModule
             where TResult : ReadModel
            => await cache.GetOrCreateAsync(
                key: key,
                factory: async (ct) => await module.Query(query),
                options: new() { Expiration = TimeSpan.FromSeconds(30) });


        // Stateless: no read model needed — derive command directly from event
        public Task<Command?> Policy(
            Event triggerEvent,
            Func<Event, Command?> policy)
                => Task.FromResult(policy(triggerEvent));

        // Enriched: load read model first, then decide command
        public async Task<Command?> Policy<T>(
            Query<T> query,
            Event triggerEvent,
            Func<T?, Event, Command?> policy)
            where T : ReadModel?
        {
            var r = await module.Query(query);
            return policy(r, triggerEvent);
        }

        // Publish policy: load read model, pass to handler, publish if non-null.
        // Use a non-nullable handler return type to signal "always publishes"; nullable for conditional.
        public async Task Policy<T, TEvent>(
            Query<T> query,
            TEvent triggerEvent,
            Func<T?, TEvent, IntegrationEvent?> policy,
            Func<IntegrationEvent, Task> pub)
            where T : ReadModel?
            where TEvent : Event
        {
            var r = await module.Query(query);
            var integrationEvent = policy(r, triggerEvent);
            if (integrationEvent is not null)
                await pub(integrationEvent);
        }
    }
}
