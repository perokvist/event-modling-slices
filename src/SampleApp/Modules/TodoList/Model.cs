using System.Security.Cryptography;
using System.Text;

namespace SampleApp.Modules.TodoList;

public record Projector<TEvent, TState>(
    TState InitialState,
    Func<TState, TEvent, TState> Evolve);

public record Projection<TEvent, TState>(
    TState InitialState,
    Func<TState, TEvent, TState> Evolve,
    Func<TState, bool>? Filter = null);

public interface ITodoItem
{
    Guid Id { get; }
    TodoStatus Status { get; }
    int Attempts { get; }
}

public record TodoState(
    IReadOnlyList<ITodoItem> Pending,
    IReadOnlyList<ITodoItem> Completed)
{
    public static readonly TodoState Empty = new([], []);
}

public record TodoItem<T>(
    Guid Id,
    T Payload,
    TodoStatus Status,
    int Attempts = 0) : ITodoItem;

public enum TodoStatus
{
    Pending,
    Completed,
    Failed
}

public record TodoPayload(string EventName, Guid EventId);

public record TodoQueued(Guid Id, Guid TodoId, TodoPayload Payload) : DomainEvent(Id);
public record TodoCompleted(Guid Id, Guid TodoId) : DomainEvent(Id);
public record TodoFailed(Guid Id, Guid TodoId, string Reason, int Attempts) : DomainEvent(Id);

public static class TodoIds
{
    public static Guid CreateDeterministic(string scope, Guid sourceEventId)
    {
        var input = $"{scope}:{sourceEventId:N}";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes);
    }
}

public record TodoListReadModel(TodoState State) : ReadModel;
public record GetTodoListQuery() : Query<TodoListReadModel>;

