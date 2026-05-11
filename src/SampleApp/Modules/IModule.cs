namespace SampleApp.Modules;

public interface IModule
{
    public Task<Result> Dispatch(Command command);
    public Task When(Event @event);
    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?;
}
