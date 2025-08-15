using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Minis.Api.Models;
using Minis.Api.Services;

namespace Minis.Api;

public static class Customization
{
    public static void Register(WebApplication app)
    {
        app.MapPost("/api/customization/update", async (CustomizationRequest request) =>
        {
            string errorLog = "";
            int rowsAffected = 0;
            string updatedJson = "";

            try
            {
                const string connString = "Server=localhost\\SQLEXPRESS02;Database=MINIS;Trusted_Connection=True;TrustServerCertificate=True;";
                string existingJson;

                using (var conn = new SqlConnection(connString))
                {
                    existingJson = await conn.ExecuteScalarAsync<string>(
                        "SELECT Customization FROM miniApps WHERE id = @id",
                        new { id = request.ShopId }
                    ) ?? "{}";
                }

                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson)
                           ?? new Dictionary<string, JsonElement>();

                dict[request.Key] = JsonDocument.Parse(JsonSerializer.Serialize(request.Value)).RootElement;

                updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                using (var conn = new SqlConnection(connString))
                {
                    rowsAffected = await conn.ExecuteAsync(
                        "UPDATE miniApps SET Customization = @cust WHERE id = @id",
                        new { cust = updatedJson, id = request.ShopId }
                    );
                }
            }
            catch (Exception ex)
            {
                errorLog = ex.ToString();
                return Results.Problem("Server error: " + ex.Message);
            }

            try
            {
                await MinisJsonService.Generate(int.Parse(request.ShopId));
            }
            catch { }

            return Results.Ok(new
            {
                status = "updated",
                shopId = request.ShopId,
                key = request.Key,
                rowsAffected,
                updatedJson,
                errorLog
            });
        });
    }
}