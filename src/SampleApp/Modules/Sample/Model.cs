namespace SampleApp.Modules.Sample;

public record GameEvent(Guid Id) : DomainEvent(Id);
public record GameCommand(Guid Id) : Command(Id);

