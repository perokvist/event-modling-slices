using DaprEventStore;
using SampleApp.Modules.Game.EmailSender;
using SampleApp.Modules.Game.MakeMove;
using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game;

public class GameModule(
    IEventStore store,
    IStateStore stateStore) : IModule
{
    private const string EmailSenderStateId = "email-sender";

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
        => throw new NotImplementedException();

    public Task When(Event @event)
        => TodoAutomationFlow.ApplyAsync<EmailSenderState, GameStarted>(
            stateStore: stateStore,
            stateId: EmailSenderStateId,
            projection: EmailSenderProjection.TodoList,
            @event: @event,
            execute: EmailSenderFunction.Execute,
            dispatch: Dispatch);
}
