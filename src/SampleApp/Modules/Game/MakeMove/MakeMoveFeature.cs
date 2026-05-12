using Microsoft.AspNetCore.Mvc;

namespace SampleApp.Modules.Game.MakeMove;

public static class MakeMoveFeature
{
    public static void MapEndpoints(RouteGroupBuilder app)
        => app.MapPost("/{id}/moves",
            async (GameModule m, Guid id, [FromBody] MakeMoveCommand cmd) =>
            {
                return await m.Dispatch(
                    command: cmd with { Id = id },
                    onSuccess: () => Results.Ok());
            });
}
