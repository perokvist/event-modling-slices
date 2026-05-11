// =============================================================================
// Example: ASP.NET Minimal API endpoint for the CreateOrder command
// =============================================================================
// File: src/App/Modules/Order/CreateOrder/CreateOrderFeature.cs
//
// The feature wires an HTTP endpoint to the module's Dispatch method.
// The module is injected via DI. Dispatch is an extension method from
// ModuleExtensions that calls IModule.Dispatch and maps Result → IResult.
//
// Pattern:
//   1. Generate a new aggregate ID (Guid.NewGuid())
//   2. Override the command's Id with the server-generated value
//   3. Dispatch through the module
//   4. Return an appropriate HTTP response on success
// =============================================================================

using Microsoft.AspNetCore.Mvc;

namespace App.Modules.Order.CreateOrder;

public static class CreateOrderFeature
{
    public static void MapEndpoints(RouteGroupBuilder app)
     => app.MapPost("/",
             async (OrderModule m, [FromBody] CreateOrderCommand cmd) =>
             {
                 var id = Guid.NewGuid();
                 return await m.Dispatch(
                     command: cmd with { Id = id },
                     onSuccess: () => Results.Created($"/orders/{id}", null));
             });
}

// --- Registration in Program.cs or a module setup method ---
// var orders = app.MapGroup("/orders").WithTags("Order");
// CreateOrderFeature.MapEndpoints(orders);
