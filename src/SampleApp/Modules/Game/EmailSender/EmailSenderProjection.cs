using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game.EmailSender;

public static class EmailSenderProjection
{
    public static readonly Projection<Event, EmailSenderState> TodoList = new(
        InitialState: EmailSenderState.Empty,
        Evolve: Evolve);

    public static EmailSenderState Evolve(EmailSenderState state, Event @event)
        => @event switch
        {
            GameStarted started => Queue(state, started),
            GameStartedEmailSent completed => Complete(state, completed),
            _ => state
        };

    private static EmailSenderState Queue(EmailSenderState state, GameStarted started)
    {
        var todoId = DeterministicTodoId(started.GameId);
        if (state.Pending.Any(x => x.Id == todoId) || state.Completed.Any(x => x.Id == todoId))
            return state;

        var pending = state.Pending
            .Concat([new EmailSenderTodoItem(
                todoId,
                started.GameId,
                started.Name,
                started.EventId,
                EmailSenderTodoStatus.Pending)])
            .ToArray();

        return state with { Pending = pending };
    }

    private static EmailSenderState Complete(EmailSenderState state, GameStartedEmailSent completed)
    {
        var todoId = DeterministicTodoId(completed.GameId);
        var existing = state.Pending.FirstOrDefault(x => x.Id == todoId) as EmailSenderTodoItem;
        if (existing is null)
            return state;

        var pending = state.Pending.Where(x => x.Id != todoId).ToArray();
        var terminal = state.Completed
            .Where(x => x.Id != todoId)
            .Concat([existing with { Status = EmailSenderTodoStatus.Completed }])
            .ToArray();

        return state with { Pending = pending, Completed = terminal };
    }

    private static Guid DeterministicTodoId(Guid gameId)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var input = $"email-sender:{gameId:N}";
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
