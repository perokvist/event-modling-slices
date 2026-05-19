using SampleApp.Modules;
using SampleApp.Modules.TodoList;

namespace SampleApp.Tests.Modules.TodoList;

public class TodoProjectionTests
{
    [Fact]
    public void Queue_deduplicates_by_todo_id()
    {
        var todoId = Guid.NewGuid();
        var queued = new TodoQueued(Guid.NewGuid(), todoId, new TodoPayload("GameStarted", Guid.NewGuid()));

        var state = TodoProjection.Evolve(TodoState.Empty, queued);
        state = TodoProjection.Evolve(state, queued);

        Assert.Single(state.Pending);
    }

    [Fact]
    public void Complete_moves_item_from_pending_to_completed()
    {
        var todoId = Guid.NewGuid();
        var queued = new TodoQueued(Guid.NewGuid(), todoId, new TodoPayload("GameStarted", Guid.NewGuid()));
        var completed = new TodoCompleted(Guid.NewGuid(), todoId);

        var state = TodoProjection.Evolve(TodoState.Empty, queued);
        state = TodoProjection.Evolve(state, completed);

        Assert.Empty(state.Pending);
        var done = Assert.Single(state.Completed);
        Assert.Equal(TodoStatus.Completed, done.Status);
    }

    [Fact]
    public void Failed_moves_item_to_completed_with_failed_status()
    {
        var todoId = Guid.NewGuid();
        var queued = new TodoQueued(Guid.NewGuid(), todoId, new TodoPayload("MoveMade", Guid.NewGuid()));
        var failed = new TodoFailed(Guid.NewGuid(), todoId, "boom", 3);

        var state = TodoProjection.Evolve(TodoState.Empty, queued);
        state = TodoProjection.Evolve(state, failed);

        Assert.Empty(state.Pending);
        var terminal = Assert.Single(state.Completed);
        Assert.Equal(TodoStatus.Failed, terminal.Status);
        Assert.Equal(3, terminal.Attempts);
    }
}

