using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Minis.Api.Services;

public static class MinisJsonService
{
    public static async Task Generate(int appId)
    {
        Console.WriteLine($"⚙️ Starting JSON generation for appId = {appId}");

        using var db = new MinisDbContext();

        // Load products with null-safety + price multiplier for appId 1
        var rawProducts = await db.Products
            .AsNoTracking()
            .Where(p => EF.Property<int>(p, "MiniAppId") == appId &&
                        EF.Property<bool?>(p, "Status") == true)
            .OrderBy(p => EF.Property<int?>(p, "Sort") ?? 9999)
            .Select(p => new
            {
                Id = EF.Property<int>(p, "Id"),
                SquareId = EF.Property<string?>(p, "SquareId"),
                Name = EF.Property<string?>(p, "Name"),

                // Apply *1.24 only for appId 1
                Price = appId == 3
                    ? EF.Property<decimal?>(p, "Price") * 1.24m
                    : EF.Property<decimal?>(p, "Price"),

                Category = EF.Property<string?>(p, "Category"),
                Image = EF.Property<string?>(p, "Image"),
                JsonData = EF.Property<string?>(p, "JsonData"),
                Sort = EF.Property<int?>(p, "Sort")
            })
            .ToListAsync();

        // Map into DTOs
        var products = rawProducts.Select(p =>
        {
            string description = "";
            JsonElement? options = null;

            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(p.JsonData) ? "{}" : p.JsonData);

                if (json.RootElement.TryGetProperty("description", out var desc))
                    description = desc.GetString() ?? "";

                if (json.RootElement.TryGetProperty("productOptions", out var opts))
                    options = opts.Clone();
            }
            catch { /* ignore bad JSON */ }

            return new ProductDto
            {
                ProductId = p.Id,
                SquareId = p.SquareId,
                Name = p.Name ?? "",
                Price = p.Price ?? 0m,
                Category = p.Category ?? "",
                Image = p.Image ?? "",
                Description = description,
                Options = options,
                Sort = p.Sort ?? 9999
            };
        }).ToList();

        // Load layout JSON
        var layoutString = await db.MiniApps
            .Where(m => m.Id == appId)
            .Select(m => m.Customization)
            .FirstOrDefaultAsync() ?? "{}";

        JsonElement layoutJsonElement;
        try
        {
            layoutJsonElement = JsonDocument.Parse(layoutString).RootElement.Clone();
        }
        catch
        {
            layoutJsonElement = JsonDocument.Parse("{}").RootElement.Clone();
        }

        // Combine layout and products
        var combined = new
        {
            canvasTexts = layoutJsonElement,
            products = products
        };

        var jsonOutput = JsonSerializer.Serialize(combined, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonElementConverter() }
        });

        // Save to file
        var outputDir = @"C:\inetpub\ftproot\Native\Native\images\json";
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, $"shop{appId}.json");
        await File.WriteAllTextAsync(filePath, jsonOutput);

        Console.WriteLine($"✅ Generated JSON for MiniApp {appId} to {filePath}");
    }

    public class ProductDto
    {
        public int ProductId { get; set; }
        public string? SquareId { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string Category { get; set; } = "";
        public string Image { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonElement? Options { get; set; }
        public int Sort { get; set; }
    }

    public class JsonElementConverter : JsonConverter<JsonElement?>
    {
        public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonDocument.ParseValue(ref reader).RootElement;

        public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                value.Value.WriteTo(writer);
            else
                writer.WriteNullValue();
        }
    }
}