using Microsoft.AspNetCore.Mvc;
using SampleApp.Modules.Sample.GetGame;

namespace SampleApp.Modules.Sample.StartGame;

public static class StartGameFeature
{
    public static void MapEndpoints(RouteGroupBuilder app)
     => app.MapPost("/",
            async (GameModule m, [FromBody] StartGameCommand cmd) =>
            {
                var id = Guid.NewGuid();
                return await m.Dispatch(
                    command: cmd with { Id = id },
                    onSuccess: () => Results.CreatedAtRoute(nameof(GetGameQuery), new { id }));
            });
}

