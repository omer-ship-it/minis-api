#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Minis.Services
{
    internal static class InvoiceEmailHelper
    {
        /// <summary>
        /// Parses the raw submitOrder JSON and sends a classic invoice email via IEmailService.
        /// If overrideOrderId is provided, it is used in the subject (recommended for /submitOrder).
        /// </summary>
        public static async Task SendInvoiceFromRawJsonAsync(
            string rawJson,
            string to,
            string from,
            string fromName,
            HttpContext ctx,
            IEmailService email,
            int? overrideOrderId = null)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Shop name (fallback Beigel Bake)
            string shopName = "Beigel Bake";
            if (root.TryGetProperty("shopSnapshot", out var snap) &&
                snap.TryGetProperty("name", out var snapName) &&
                snapName.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(snapName.GetString()))
            {
                shopName = snapName.GetString()!;
            }

            // OrderId for subject
            int? orderId = overrideOrderId;
            if (orderId is null &&
                root.TryGetProperty("orderId", out var oid) &&
                oid.ValueKind == JsonValueKind.Number &&
                oid.TryGetInt32(out var parsed))
            {
                orderId = parsed;
            }

            var subject = orderId.HasValue
                ? $"{shopName} Order #{orderId.Value} – Invoice"
                : $"{shopName} – Invoice";

            // Transform raw request JSON → shape expected by SendSimpleInvoiceAsync
            var transformed = new Dictionary<string, object?>
            {
                ["orderId"] = orderId ?? 0,
                ["shopName"] = shopName,
                ["logoUrl"] = "https://www.beigelbake.co.uk/cdn/shop/files/main-logo.jpg?v=1672516746&width=400",
                ["buttonColor"] = "#c72d22",
                ["customer"] = new
                {
                    name = root.TryGetProperty("name", out var cname) && cname.ValueKind == JsonValueKind.String ? cname.GetString() : null,
                    phone = root.TryGetProperty("delivery", out var del1) &&
                            del1.TryGetProperty("address", out var addr1) &&
                            addr1.TryGetProperty("phone", out var phone1) &&
                            phone1.ValueKind == JsonValueKind.String ? phone1.GetString() : null
                },
                ["delivery"] = new
                {
                    isDelivery = root.TryGetProperty("delivery", out var del2) &&
                                 del2.TryGetProperty("isDelivery", out var isDel) &&
                                 (isDel.ValueKind == JsonValueKind.True ||
                                  (isDel.ValueKind == JsonValueKind.String && isDel.GetString()?.ToLowerInvariant() == "true")),
                    address = root.TryGetProperty("delivery", out var del3) &&
                              del3.TryGetProperty("address", out var addr2)
                        ? new
                        {
                            address1 = addr2.TryGetProperty("address1", out var a1) ? a1.GetString() : null,
                            address2 = addr2.TryGetProperty("address2", out var a2) ? a2.GetString() : null,
                            postcode = addr2.TryGetProperty("postcode", out var pc) ? pc.GetString() : null,
                            phone = addr2.TryGetProperty("phone", out var ph) ? ph.GetString() : null
                        }
                        : null
                },
                ["scheduledFor"] = root.TryGetProperty("delivery", out var del4) &&
                                   del4.TryGetProperty("scheduledFor", out var sch) ? sch.GetString() : null,
                ["basket"] = root.TryGetProperty("basket", out var basket) && basket.ValueKind == JsonValueKind.Array
                    ? basket.EnumerateArray().Select(item => new
                    {
                        // ✅ Name fallback logic to avoid "Product 105"
                        name =
                            (item.TryGetProperty("name", out var iname) && iname.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(iname.GetString()))
                                ? iname.GetString()
                                : (item.TryGetProperty("productName", out var iprod) && iprod.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(iprod.GetString()))
                                    ? iprod.GetString()
                                    : (item.TryGetProperty("productId", out var pid) && pid.ValueKind == JsonValueKind.Number)
                                        ? $"Product {pid.GetRawText()}"
                                        : "Item",
                        modifiers = item.TryGetProperty("modifiers", out var imod) ? imod.GetString() : null,
                        quantity = item.TryGetProperty("quantity", out var iqty) && iqty.TryGetInt32(out var qv) ? qv : 1,
                        price = item.TryGetProperty("price", out var iprice) && iprice.TryGetDecimal(out var pv) ? pv : 0m
                    }).ToArray()
                    : Array.Empty<object>(),
                ["subtotal"] = root.TryGetProperty("basket", out var bEl) && bEl.ValueKind == JsonValueKind.Array
                    ? bEl.EnumerateArray().Sum(x =>
                    {
                        var price = x.TryGetProperty("price", out var pr) && pr.TryGetDecimal(out var pd) ? pd : 0m;
                        var qty = x.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qv) ? qv : 1;
                        return price * qty;
                    })
                    : 0m,
                ["deliveryFee"] = 0m,
                ["total"] = root.TryGetProperty("total", out var tot) && tot.TryGetDecimal(out var tv) ? tv : 0m
            };

            var jsonPayload = JsonSerializer.SerializeToElement(transformed);
            await email.SendSimpleInvoiceAsync(
                to: to,
                subject: subject,
                payload: jsonPayload,
                fromName: fromName,
                fromEmail: from,
                ct: ctx.RequestAborted
            );
        }
    }
}