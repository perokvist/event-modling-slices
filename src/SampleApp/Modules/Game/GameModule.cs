using DaprEventStore;
using SampleApp.Modules.Game.EmailSender;
using SampleApp.Modules.Game.GetGame;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game;

public class GameModule(
    IEventStore store,
    IStateStore stateStore,
    GameProjection gameProjection) : IModule
{
    public Task<Result> Dispatch(Command command)
        => command switch
        {
            StartGameCommand cmd => store.Execute(cmd, new GameDecider()).ToResultAsync(),
            MakeMoveCommand cmd => store.Execute(cmd, new GameDecider()).ToResultAsync(),
            SendGameStartedEmailCommand cmd => store.ExecuteStateless(
                    cmd,
                    SendGameStartedEmailGateway.Execute,
                    When)
                .ToResultAsync(),
            _ => throw new NotImplementedException()
        };

    public ValueTask<T?> Query<T>(Query<T> query) where T : ReadModel?
        => query switch
        {
            GetGameQuery q => new ValueTask<T?>((T?)(object?)gameProjection.Get(q.Id)),
            _ => throw new NotImplementedException()
        };

    public async Task When(Event @event)
    {
        switch (@event)
        {
            case GameStarted started:
                gameProjection.Apply(started);
                break;
            case MoveMade moveMade:
                gameProjection.Apply(moveMade);
                break;
        }

        await TodoAutomationFlow.ApplyAsync<EmailSenderState, GameStarted>(
            stateStore: stateStore,
            stateId: "email-sender",
            projection: EmailSenderProjection.TodoList,
            @event: @event,
            execute: EmailSenderFunction.Execute,
            dispatch: Dispatch);
    }
}
