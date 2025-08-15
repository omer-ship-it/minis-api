using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minis.Services;

public static class DeliveryStatusService
{
    // Call this from webhooks or admin tools
    public static async Task SaveAsync(int orderId, DeliveryStatusDocument doc, IConfiguration config)
    {
        // Where to save files (configure in appsettings.json or env var)
        var baseDir = config["StatusJson:OutputDir"]
            ?? @"C:\inetpub\ftproot\Native\Native\images\orders"; // fallback

        Directory.CreateDirectory(baseDir);

        // Optional: keep per-order file
        var filePath = Path.Combine(baseDir, $"{orderId}.json");

        // Stamp time on server side
        doc.OrderId = orderId;
        doc.UpdatedAt = DateTime.UtcNow;

        // Serialize compact (or set WriteIndented = true while debugging)
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await File.WriteAllTextAsync(filePath, json);

        // (Optional) If you also want to push to Cloudflare R2, call a helper here:
        // await R2Helper.PutAsync($"orders/{orderId}.json", json, "application/json", config);
    }
}

// ---------- Data models ----------

public class DeliveryStatusDocument
{
    public int OrderId { get; set; }
    public string? Provider { get; set; }          // "gophr" | "orkestro" | etc.
    public string Status { get; set; } = "unknown"; // "confirmed" | "driver_assigned" | "on_the_way" | "arriving" | "delivered" | "cancelled"
    public DriverInfo? Driver { get; set; }
    public GeoPoint? Location { get; set; }
    public int? EtaMinutes { get; set; }           // estimated arrival in minutes
    public DateTime UpdatedAt { get; set; }        // server time (UTC)
    public object? Raw { get; set; }               // (optional) original webhook payload (for debugging)
}

public class DriverInfo
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Vehicle { get; set; }
    public string? Plate { get; set; }
}

public class GeoPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? Accuracy { get; set; }  // meters, optional
    public double? Bearing { get; set; }   // degrees, optional
}