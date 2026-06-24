using SampleApp.Modules;
using SampleApp.Modules.Game.EmailSender;
using SampleApp.Modules.Game.SendGameStartedEmail;
using SampleApp.Modules.Game.StartGame;

namespace SampleApp.Tests.Modules.Game.EmailSender;

public class EmailSenderProjectionTests
{
    [Fact]
    public void Queue_creates_pending_todo_from_game_started()
    {
        var gameId = Guid.NewGuid();
        var started = new GameStarted(gameId, "Test Game");

        var state = EmailSenderProjection.Evolve(EmailSenderState.Empty, started);

        Assert.Single(state.Pending);
        var item = state.Pending.First();
        Assert.Equal(DeterministicTodoId(gameId), item.Id);
        Assert.Equal(gameId, item.GameId);
        Assert.Equal(EmailSenderTodoStatus.Pending, item.Status);
    }

    [Fact]
    public void Queue_deduplicates_by_game_id()
    {
        var gameId = Guid.NewGuid();
        var first = new GameStarted(gameId, "Test Game");
        var second = new GameStarted(gameId, "Test Game");

        var state = EmailSenderProjection.Evolve(EmailSenderState.Empty, first);
        state = EmailSenderProjection.Evolve(state, second);

        Assert.Single(state.Pending);
    }

    [Fact]
    public void Complete_moves_item_from_pending_to_completed_using_result_event()
    {
        var gameId = Guid.NewGuid();
        var started = new GameStarted(gameId, "Test Game");
        var completed = new GameStartedEmailSent(gameId);

        var state = EmailSenderProjection.Evolve(EmailSenderState.Empty, started);
        state = EmailSenderProjection.Evolve(state, completed);

        Assert.Empty(state.Pending);
        var done = Assert.Single(state.Completed);
        Assert.Equal(EmailSenderTodoStatus.Completed, done.Status);
    }

    private static Guid DeterministicTodoId(Guid gameId)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var input = $"email-sender:{gameId:N}";
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
