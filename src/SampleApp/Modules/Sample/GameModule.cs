using DaprEventStore;
using SampleApp.Modules.Sample.StartGame;

namespace SampleApp.Modules.Sample;

public class GameModule(IEventStore store, Func<IntegrationEvent, Task> pub) : IModule
{
    public Task<Result> Dispatch(Command command)
        => command switch
        {
            StartGameCommand cmd => store.Execute(cmd, new GameDecider()).ToResultAsync(),
            _ => throw new NotImplementedException()
        };

    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
    {
        throw new NotImplementedException();
    }

    public Task When(Event @event)
    {
        throw new NotImplementedException();
    }
}
