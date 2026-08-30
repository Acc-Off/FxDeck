using FxDeck.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FxDeck.Web;

/// <summary>Phone-facing API (token required). Reachable on both listeners.</summary>
public static class DeckEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Exchange the QR token for the session cookie. Rate limited per IP.
        app.MapPost("/api/deck/session", (HttpContext context, DeckAuth auth) =>
        {
            var token = context.Request.Query[DeckAuth.TokenQueryName].ToString();
            if (!auth.ValidateToken(token))
            {
                return Results.Json(new { error = "invalidToken" }, FxJson.Wire, statusCode: StatusCodes.Status401Unauthorized);
            }

            auth.IssueCookie(context);
            return Results.Json(new { ok = true }, FxJson.Wire);
        }).RequireRateLimiting(FxDeckHost.SessionRateLimitPolicy);

        // User images (design memo §3.8). The admin UI previews them too, and it has no deck cookie, so requests
        // that arrive on the loopback admin listener are let through as well.
        app.MapGet("/api/deck/assets/{hash}", (string hash, HttpContext context, DeckAuth auth, ListenerInfo listeners, AssetStore assets) =>
        {
            if (!auth.IsAuthenticated(context) && !listeners.IsAdminConnection(context.Connection))
            {
                return Results.Json(new { error = "unauthorized" }, FxJson.Wire, statusCode: StatusCodes.Status401Unauthorized);
            }

            var png = assets.Read(hash);
            if (png is null)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Bytes(png, "image/png");
        });

        var deck = app.MapGroup("/api/deck").AddEndpointFilter<DeckSessionFilter>();

        deck.MapGet("/profile", (DeckHub hub) => Results.Json(hub.BuildHello(), FxJson.Wire));

        deck.MapGet("/ws", async (HttpContext context, DeckHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket expected");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.HandleAsync(socket, context.RequestAborted);
        });
    }

    /// <summary>Rejects requests without a valid session cookie and slides the cookie lifetime otherwise.</summary>
    private sealed class DeckSessionFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var auth = http.RequestServices.GetRequiredService<DeckAuth>();
            if (!auth.IsAuthenticated(http))
            {
                return Results.Json(new { error = "unauthorized" }, FxJson.Wire, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!http.WebSockets.IsWebSocketRequest)
            {
                auth.IssueCookie(http); // sliding expiration
            }

            return await next(context);
        }
    }
}
