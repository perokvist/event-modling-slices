using SampleApp.Modules.Game;
using SampleApp.Modules.Game.EmailSender;
using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.EmailSender;

public class EmailSenderFunctionTests
{
    [Fact]
    public void Execute_returns_command_when_pending_todo_exists()
    {
        var gameId = Guid.NewGuid();
        var trigger = new GameStarted(gameId, "Test Game");
        var state = EmailSenderProjection.Evolve(EmailSenderState.Empty, trigger);

        var command = EmailSenderFunction.Execute(state, trigger);

        Assert.NotNull(command);
        Assert.IsType<SendGameStartedEmailCommand>(command);
        var emailCmd = (SendGameStartedEmailCommand)command;
        Assert.Equal(gameId, emailCmd.GameId);
        Assert.Equal("Test Game", emailCmd.GameName);
    }

    [Fact]
    public void Execute_returns_null_when_no_pending_todo()
    {
        var gameId = Guid.NewGuid();
        var trigger = new GameStarted(gameId, "Test Game");
        var state = EmailSenderState.Empty;

        var command = EmailSenderFunction.Execute(state, trigger);

        Assert.Null(command);
    }

    [Fact]
    public void Execute_returns_null_when_already_completed()
    {
        var gameId = Guid.NewGuid();
        var trigger = new GameStarted(gameId, "Test Game");
        var state = EmailSenderProjection.Evolve(EmailSenderState.Empty, trigger);
        state = EmailSenderProjection.Evolve(state, new GameStartedEmailSent(gameId));

        var command = EmailSenderFunction.Execute(state, trigger);

        Assert.Null(command);
    }
}
