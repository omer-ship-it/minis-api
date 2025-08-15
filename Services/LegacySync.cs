using System;
using System.Data.SqlClient;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Minis.Delivery; // if you also use UpdateLegacyCourierAsync with DeliveryJobResult

public static class LegacySync
{
    public static async Task<int> SyncToLegacyAsync(
        IConfiguration cfg,
        object submitRequest,          // DTO-agnostic: we read via JSON
        int minisOrderId,              // MINIS order id (we stash it in comment)
        string stripeChargeId,
        string? destinationPaymentId,
        decimal subtotal,
        decimal deliveryFee)
    {
        var legacyConnStr = cfg.GetConnectionString("LegacyMinitel");
        if (string.IsNullOrWhiteSpace(legacyConnStr))
            return 0;

        // --- Read from any request shape via JSON (case-insensitive) ---
        var root = JsonSerializer.SerializeToElement(submitRequest);

        string Name(params string[] p) => GetStringCI(root, p) ?? "Customer";
        string? Str(params string[] p) => GetStringCI(root, p);
        int Int(params string[] p) => GetIntCI(root, p) ?? 0;
        double? Dbl(params string[] p) => GetDoubleCI(root, p);

        var name = Name("name");
        var email = Str("email");

        // shop id (MiniAppId) — default to 1 if missing/invalid
        var miniAppId = Int("miniAppId");
        if (miniAppId <= 0) miniAppId = 1;

        var notes = Str("delivery", "notes")
                 ?? Str("delivery", "instructions")
                 ?? Str("delivery", "address", "dir");

        // Delivery.Address — case-insensitive
        var addr1 = Str("delivery", "address", "address1");
        var addr2 = Str("delivery", "address", "address2");
        var postcode = Str("delivery", "address", "postcode");
        var phone = Str("delivery", "address", "phone");
        var toName = Str("delivery", "address", "name") ?? name;

        // tolerate lat/lng/long/latitude/longitude variants
        var lat = Dbl("delivery", "address", "lat")
               ?? Dbl("delivery", "address", "latitude");
        var lng = Dbl("delivery", "address", "lng")
               ?? Dbl("delivery", "address", "long")
               ?? Dbl("delivery", "address", "longitude");

        var scheduledFor = Str("delivery", "scheduledFor");

        await using var conn = new SqlConnection(legacyConnStr);
        await conn.OpenAsync();

        // --- Upsert customer ---
        long legacyCustomerId;
        if (!string.IsNullOrWhiteSpace(email))
        {
            legacyCustomerId = await conn.ExecuteScalarAsync<long?>(
                "SELECT TOP 1 ID FROM customers WHERE email = @email ORDER BY ID",
                new { email }) ?? 0;

            if (legacyCustomerId == 0)
            {
                legacyCustomerId = await conn.ExecuteScalarAsync<long>(
                    @"INSERT INTO customers (name, email, minitel, lastModified)
                      VALUES (@name, @email, 1, CONVERT(nvarchar(200), GETDATE(), 120));
                      SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                    new { name, email });
            }
            else
            {
                await conn.ExecuteAsync(
                    @"UPDATE customers
                      SET name = CASE WHEN (name IS NULL OR LTRIM(RTRIM(name)) = '') THEN @name ELSE name END,
                          lastModified = CONVERT(nvarchar(200), GETDATE(), 120)
                      WHERE ID = @id",
                    new { id = legacyCustomerId, name });
            }
        }
        else
        {
            legacyCustomerId = await conn.ExecuteScalarAsync<long>(
                @"INSERT INTO customers (name, email, minitel, lastModified)
                  VALUES (@name, NULL, 1, CONVERT(nvarchar(200), GETDATE(), 120));
                  SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { name });
        }

        var legacyCustomerIdStr = legacyCustomerId.ToString();

        // --- Insert address if present ---
        int? addressId = null;
        if (!string.IsNullOrWhiteSpace(addr1) || !string.IsNullOrWhiteSpace(postcode))
        {
            addressId = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO Addresses
                  (customerId, address1, address2, postcode, city, dir, status, lat, lng, name, phone, lastModified)
                  VALUES (@customerId, @address1, @address2, @postcode, @city, @dir, @status, @lat, @lng, @name, @phone, GETDATE());
                  SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new
                {
                    customerId = legacyCustomerIdStr,
                    address1 = addr1 ?? "",
                    address2 = addr2 ?? "",
                    postcode = postcode ?? "",
                    city = "",
                    dir = notes ?? "",
                    status = 1,
                    lat = lat?.ToString() ?? "",
                    lng = lng?.ToString() ?? "",
                    name = toName,
                    phone = phone
                });
        }

        // Delivery time (same resolver you already use)
        var whenLondon = DeliveryMapper.ResolveLondonWhen(scheduledFor);

        // --- Insert legacy order (stash MINIS id for traceability) ---
        var legacyOrderId = await conn.ExecuteScalarAsync<int>(
@"
INSERT INTO orders
(
    shop, status, oldStatus, customerId, addressId,
    pickupTime, deliveryTime, eta,
    deliveryFee, deliveryCost,
    GophrId, deliveryId, courierId,
    lat, lng, driver, driverPhone,
    platform, Refund, Discount,
    paymentId, transferId, tempPaymentId,
    distance, deliveryPrice,
    pop, pod, pickup, minitel,
    lastModified, isPrint, comment, sent,
    EmailSentForOnTheWay, EmailSentForDelivered, csySent,
    squareOrderId
)
VALUES
(
    @shop, @status, @oldStatus, @customerId, @addressId,
    NULL, @deliveryTime, NULL,
    @deliveryFee, NULL,
    NULL, NULL, NULL,
    @lat, @lng, NULL, NULL,
    @platform, NULL, NULL,
    @paymentId, @transferId, NULL,
    NULL, @deliveryPrice,
    NULL, NULL, NULL, 1,
    GETDATE(), 0, @comment, 0,
    0, 0, 0,
    NULL
);
SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new
            {
                shop = miniAppId.ToString(),          // 👈 now correct (defaults to 1)
                status = "1",
                oldStatus = "0",
                customerId = legacyCustomerIdStr,
                addressId,
                deliveryTime = whenLondon.UtcDateTime,
                deliveryFee = deliveryFee.ToString("0.00"),
                lat = lat?.ToString() ?? "",
                lng = lng?.ToString() ?? "",
                platform = "MINIS",
                paymentId = stripeChargeId,
                transferId = destinationPaymentId,
                deliveryPrice = deliveryFee.ToString("0.00"),
                comment = $"MINIS#{minisOrderId}"
            });

        // --- Basket lines (case-insensitive) ---
        if (TryGetCI(root, out var basketEl, "basket") && basketEl.ValueKind == JsonValueKind.Array
         || TryGetCI(root, out basketEl, "Basket") && basketEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in basketEl.EnumerateArray())
            {
                var minisProductId = GetIntCI(line, "productId") ?? 0;
                var quantity = GetDoubleCI(line, "quantity") ?? 0.0;
                var price = GetDoubleCI(line, "price") ?? 0.0;
                var modifiers = GetStringCI(line, "modifiers") ?? "";

                // map MINIS productId -> legacy Products.Id via minisProductId
                var legacyProductId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT TOP 1 Id FROM dbo.Products WHERE minisProductId = @minisId",
                    new { minisId = minisProductId }) ?? 0;

                await conn.ExecuteAsync(
                    @"INSERT INTO basket
                      (customerId, orderId, productId, amount, price, cost, choices, lastModified)
                      VALUES (@customerId, @orderId, @productId, @amount, @price, @cost, @choices, GETDATE());",
                    new
                    {
                        customerId = (int)Math.Min(legacyCustomerId, int.MaxValue),
                        orderId = legacyOrderId,
                        productId = legacyProductId,
                        amount = quantity,
                        price = price,
                        cost = 0.0,
                        choices = modifiers
                    });
            }
        }

        return legacyOrderId;
    }

    // ---------- JSON helpers (case-insensitive traversal) ----------
    private static string? GetStringCI(JsonElement obj, params string[] path)
        => TryGetCI(obj, out var el, path) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? GetIntCI(JsonElement obj, params string[] path)
        => TryGetCI(obj, out var el, path) && el.TryGetInt32(out var v) ? v : (int?)null;

    private static double? GetDoubleCI(JsonElement obj, params string[] path)
    {
        if (!TryGetCI(obj, out var el, path)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var ds)) return ds;
        return null;
    }

    private static bool TryGetCI(JsonElement obj, out JsonElement found, params string[] path)
    {
        found = obj;
        foreach (var seg in path)
        {
            if (found.ValueKind != JsonValueKind.Object)
                return false;

            var matched = false;
            foreach (var prop in found.EnumerateObject())
            {
                if (string.Equals(prop.Name, seg, StringComparison.OrdinalIgnoreCase))
                {
                    found = prop.Value;
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }

    // (optional) if you also want to update courier ids later:
    public static async Task UpdateLegacyCourierAsync(
        IConfiguration cfg, int legacyOrderId, DeliveryJobResult delivery)
    {
        var legacyConnStr = cfg.GetConnectionString("LegacyMinitel");
        if (string.IsNullOrWhiteSpace(legacyConnStr)) return;

        await using var conn = new SqlConnection(legacyConnStr);
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
UPDATE orders
SET
    GophrId    = CASE WHEN @provider = 'gophr' THEN @deliveryId ELSE GophrId END,
    deliveryId = @deliveryId,
    lastModified = GETDATE()
WHERE ID = @legacyOrderId;",
            new
            {
                legacyOrderId,
                provider = delivery.Provider?.ToLowerInvariant(),
                deliveryId = delivery.DeliveryId
            });
    }
}