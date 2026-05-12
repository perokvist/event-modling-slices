using SampleApp.Modules.Game.MakeMove;

namespace SampleApp.Modules.Game;

public record GameEvent(Guid Id) : DomainEvent(Id);
public record GameCommand(Guid Id) : Command(Id);

public record MoveMade(Guid GameId, Guid PlayerId, Move Move) : GameEvent(GameId);

