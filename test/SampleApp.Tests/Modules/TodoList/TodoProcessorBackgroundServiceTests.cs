using System.Threading.Channels;
using DaprEventStore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using SampleApp.Modules.TodoList;

namespace SampleApp.Tests.Modules.TodoList;

public class TodoProcessorBackgroundServiceTests
{
    [Fact]
    public async Task ProcessAsync_success_emits_TodoCompleted()
    {
        var queue = Channel.CreateUnbounded<TodoItem<TodoPayload>>();
        var store = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddScoped<IEventStore>(_ => store)
            .BuildServiceProvider();
        var service = new TodoProcessorBackgroundService(
            queue,
            new PassExecutor(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new TodoAutomationOptions { MaxAttempts = 2, PublishToPubSub = false },
            NullLogger<TodoProcessorBackgroundService>.Instance);

        var todo = new TodoItem<TodoPayload>(Guid.NewGuid(), new TodoPayload("GameStarted", Guid.NewGuid()), TodoStatus.Pending);
        await service.ProcessAsync(todo, CancellationToken.None);

        var events = await store.LoadEventStreamAsync(TodoProcessorBackgroundService.TodoStream, 0).ToListAsync();
        var completed = Assert.Single(events);
        Assert.Equal(nameof(TodoCompleted), completed.EventName);
    }

    [Fact]
    public async Task ProcessAsync_retries_then_emits_TodoFailed()
    {
        var queue = Channel.CreateUnbounded<TodoItem<TodoPayload>>();
        var store = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddScoped<IEventStore>(_ => store)
            .BuildServiceProvider();
        var service = new TodoProcessorBackgroundService(
            queue,
            new FailExecutor(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new TodoAutomationOptions { MaxAttempts = 2, PublishToPubSub = false },
            NullLogger<TodoProcessorBackgroundService>.Instance);

        var todo = new TodoItem<TodoPayload>(Guid.NewGuid(), new TodoPayload("MoveMade", Guid.NewGuid()), TodoStatus.Pending);
        await service.ProcessAsync(todo, CancellationToken.None);
        var retried = await queue.Reader.ReadAsync(CancellationToken.None);
        await service.ProcessAsync(retried, CancellationToken.None);

        var events = await store.LoadEventStreamAsync(TodoProcessorBackgroundService.TodoStream, 0).ToListAsync();
        var failed = Assert.Single(events);
        Assert.Equal(nameof(TodoFailed), failed.EventName);
    }

    private sealed class PassExecutor : ITodoEffectExecutor
    {
        public Task ExecuteAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailExecutor : ITodoEffectExecutor
    {
        public Task ExecuteAsync(TodoItem<TodoPayload> todo, CancellationToken cancellationToken)
            => throw new InvalidOperationException("fail");
    }
}

