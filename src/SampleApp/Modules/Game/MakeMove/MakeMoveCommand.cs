using SampleApp.Modules.Game.MakeMove;

namespace SampleApp.Modules.Game.MakeMove;

public record MakeMoveCommand(Guid Id, Guid PlayerId, Move Move) : GameCommand(Id);
