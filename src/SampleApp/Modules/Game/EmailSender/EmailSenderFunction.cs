using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Modules.Game.EmailSender;

/// <summary>
/// Main EmailSender automation function (decider-inspired).
/// Given current automation state and a GameStarted trigger event,
/// decides whether to dispatch SendGameStartedEmailCommand.
/// </summary>
public static class EmailSenderFunction
{
    public static Command? Execute(EmailSenderState state, GameStarted trigger)
    {
        var todoId = DeterministicTodoId(trigger.GameId);

        if (state.Completed.Any(x => x.Id == todoId))
            return null;
        if (!state.Pending.Any(x => x.Id == todoId))
            return null;

        return new SendGameStartedEmailCommand(Guid.NewGuid(), trigger.GameId, trigger.Name);
    }

    private static Guid DeterministicTodoId(Guid gameId)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var input = $"email-sender:{gameId:N}";
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
