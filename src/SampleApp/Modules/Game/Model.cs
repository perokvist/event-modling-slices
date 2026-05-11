namespace SampleApp.Modules.Game;

public record GameEvent(Guid Id) : DomainEvent(Id);
public record GameCommand(Guid Id) : Command(Id);

