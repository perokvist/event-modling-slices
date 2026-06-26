using SampleApp.Modules.Game.MakeMove;

namespace SampleApp.Modules.Game.GetGame;

public record GameMoveView(Guid PlayerId, Move Move);
