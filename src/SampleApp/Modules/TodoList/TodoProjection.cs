namespace SampleApp.Modules.TodoList;

public static class TodoProjection
{
    public static readonly Projection<Event, TodoState> TodoList = new(
        InitialState: TodoState.Empty,
        Evolve: Evolve);

    public static TodoState Evolve(TodoState state, Event @event)
        => @event switch
        {
            TodoQueued queued => Queue(state, queued),
            TodoCompleted completed => Complete(state, completed),
            TodoFailed failed => Fail(state, failed),
            _ => state
        };

    private static TodoState Queue(TodoState state, TodoQueued queued)
    {
        if (state.Pending.Any(x => x.Id == queued.TodoId) || state.Completed.Any(x => x.Id == queued.TodoId))
            return state;

        var pending = state.Pending
            .Concat([new TodoItem<TodoPayload>(queued.TodoId, queued.Payload, TodoStatus.Pending)])
            .ToArray();

        return state with { Pending = pending };
    }

    private static TodoState Complete(TodoState state, TodoCompleted completed)
    {
        var existing = state.Pending.FirstOrDefault(x => x.Id == completed.TodoId) as TodoItem<TodoPayload>;
        if (existing is null)
            return state;

        var pending = state.Pending.Where(x => x.Id != completed.TodoId).ToArray();
        var terminal = state.Completed
            .Where(x => x.Id != completed.TodoId)
            .Concat([existing with { Status = TodoStatus.Completed }])
            .ToArray();

        return state with { Pending = pending, Completed = terminal };
    }

    private static TodoState Fail(TodoState state, TodoFailed failed)
    {
        var existing = state.Pending.FirstOrDefault(x => x.Id == failed.TodoId) as TodoItem<TodoPayload>;
        if (existing is null)
            return state;

        var pending = state.Pending.Where(x => x.Id != failed.TodoId).ToArray();
        var terminal = state.Completed
            .Where(x => x.Id != failed.TodoId)
            .Concat([existing with { Status = TodoStatus.Failed, Attempts = failed.Attempts }])
            .ToArray();

        return state with { Pending = pending, Completed = terminal };
    }
}

