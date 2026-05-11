using DaprEventStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SampleApp.Modules;
using System.Reactive.Subjects;

namespace SampleApp.Tests;

public class Fixture : WebApplicationFactory<global::Program>
{
    private Action<string> log = s => { };
    private TestModule Module { get; set; } = new(new());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
     => builder
        .ConfigureLogging(x =>
                x
                .AddDebug()
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new DelegateLoggerProvider(s => log(s))))
        .ConfigureTestServices(services => services
            .AddSingleton<Func<IntegrationEvent, Task>>(Module.When)
            .AddSingleton<IEventStore, InMemoryEventStore>()
            .AddModule(Module)
        );

    public Fixture WithLogging(Action<string> log)
    {
        this.log = msg =>
        {
            try
            {
                log(msg);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("active test", StringComparison.OrdinalIgnoreCase))
            {
            }
        };
        return this;
    }

    public async Task<Fixture> WithHistory<TState>(
       Decider<Command, Event, TState> decider,
       params Event[] history)
       where TState : State
    {
        var store = base.Services.GetRequiredService<IEventStore>();
        var r = decider.RouteEvents(history);

        if (r.Count() == 1)
        {
            var eventData = r.First().Events.Select(e => EventData.Create(e.GetType().Name, e)).ToArray();
            await store.AppendToStreamAsync(r.First().StreamName, 0, eventData);

        }
        var state = history.Aggregate(decider.InitialState, decider.Evolve);

        return this;
    }


    public Task Test(Func<HttpClient, Task> f)
        => f(CreateClient());

    public Task Test(Func<HttpClient, IObservable<Event>, Task> f)
        => f(CreateClient(), Module);
}

public static class FixtureExtensions
{
    public static async Task Test(this Task<Fixture> fixture, Func<HttpClient, Task> f)
        => await (await fixture).Test(f);

    public static async Task Test(this Task<Fixture> fixture, Func<HttpClient, IObservable<Event>, Task> f)
        => await (await fixture).Test(f);
}

public class TestModule(Subject<Event> pub) : IModule, IObservable<Event>
{
    public Task<Result> Dispatch(Command command) => throw new NotImplementedException();
    public Task When(Event @event)
    {
        pub.OnNext(@event);
        return Task.CompletedTask;
    }
    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel? => throw new NotImplementedException();
    public IDisposable Subscribe(IObserver<Event> observer) => pub.Subscribe(observer);
}
