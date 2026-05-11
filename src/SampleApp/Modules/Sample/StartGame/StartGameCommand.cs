namespace SampleApp.Modules.Sample.StartGame;

public record StartGameCommand(
    Guid Id, string Name) : GameCommand(Id);
