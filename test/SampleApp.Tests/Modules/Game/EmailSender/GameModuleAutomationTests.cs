using DaprEventStore;
using SampleApp.Modules;
using SampleApp.Modules.Game;
using SampleApp.Modules.Game.EmailSender;
using SampleApp.Modules.Game.GetGame;
using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.EmailSender;

public class GameModuleAutomationTests
{
    [Fact]
    public async Task When_game_started_dispatches_send_email_command()
    {
        var store = new InMemoryEventStore();
        var stateStore = new InMemoryStateStore();
        var module = new GameModule(store, stateStore, new GameProjection());
        var gameId = Guid.NewGuid();

        await module.When(new GameStarted(gameId, "Test Game"));

        var gameEvents = await LoadStreamAsync(store, $"Game-{gameId}");
        Assert.Empty(gameEvents);

        var state = await stateStore.GetAsync<EmailSenderState>("email-sender");
        Assert.NotNull(state);
        Assert.Empty(state.Pending);
        Assert.Single(state.Completed);
    }

    [Fact]
    public async Task Dispatch_send_email_command_without_todo_does_not_change_state()
    {
        var store = new InMemoryEventStore();
        var stateStore = new InMemoryStateStore();
        var module = new GameModule(store, stateStore, new GameProjection());
        var gameId = Guid.NewGuid();

        await module.Dispatch(new SendGameStartedEmailCommand(Guid.NewGuid(), gameId, "Test Game"));

        var state = await stateStore.GetAsync<EmailSenderState>("email-sender");
        Assert.Null(state);
    }

    private static async Task<List<VersionedEvent>> LoadStreamAsync(IEventStore store, string stream)
    {
        var result = new List<VersionedEvent>();
        await foreach (var item in store.LoadEventStreamAsync(stream, 0))
        {
            result.Add(item);
        }

        return result;
    }
}
