using Microsoft.AspNetCore.Mvc;
using SampleApp.Modules.Game;

namespace SampleApp.Modules.Game.GetGame;

public static class GetGameHandler
{
    public static void MapEndpoints(RouteGroupBuilder app)
     => app.MapGet("{id}", async ([FromBody] GetGameQuery q, GameModule m, LinkGenerator linkGenerator, HttpContext http) =>
            await m.Query(q) switch
            {
                null => Results.NotFound(),
                var v => Results.Ok(new ViewResponse<GameView>(
                    View: v,
                    Links:
                    [
                        new(
                        Href: linkGenerator.GetUriByName(http, nameof(GetGameQuery), values: new { q.Id })!,
                        Rel: "self",
                        Method: "GET")
                    ]))
            })
           .WithName(nameof(GetGameQuery));
}
