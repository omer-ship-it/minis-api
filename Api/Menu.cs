using Minis.Api.Models;

namespace Minis.Api;

public static class MenuApi
{
    public static void Register(WebApplication app)
    {
        app.MapGet("/api/menus", () =>
        {
            var items = new[]
            {
                new MenuItem(1, "Margherita", 9.00),
                new MenuItem(2, "Pepperoni", 11.00)
            };
            return Results.Json(items);
        });
    }
}