namespace SampleApp.Modules.Game.EmailSender;

public enum EmailSenderTodoStatus
{
    Pending,
    Completed
}

public record EmailSenderTodoItem(
    Guid Id,
    Guid GameId,
    string GameName,
    Guid SourceEventId,
    EmailSenderTodoStatus Status);

public record EmailSenderState(
    IReadOnlyList<EmailSenderTodoItem> Pending,
    IReadOnlyList<EmailSenderTodoItem> Completed)
{
    public static readonly EmailSenderState Empty = new([], []);
}
