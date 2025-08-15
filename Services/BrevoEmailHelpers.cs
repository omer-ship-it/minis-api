#nullable enable
using System.Text.Json;

namespace Minis.Services
{
    internal static class BrevoEmailHelpers
    {
        public static async Task SendClassicInvoiceFromRawAsync(
            IEmailService email,
            JsonElement root,
            int orderId,
            string defaultFromName,
            string defaultFromEmail,
            string? overrideTo,
            CancellationToken ct)
        {
            string shopName = defaultFromName;
            if (root.TryGetProperty("shopSnapshot", out var snap) &&
                snap.TryGetProperty("name", out var s) &&
                s.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(s.GetString()))
            {
                shopName = s.GetString()!;
            }

            var to = !string.IsNullOrWhiteSpace(overrideTo)
                     ? overrideTo!
                     : (root.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString()!
                        : "orders@minis.app");

            var subject = $"{shopName} Order #{orderId} – Invoice";

            var transformed = new Dictionary<string, object?>
            {
                ["orderId"] = orderId,
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
                                  (isDel.ValueKind == JsonValueKind.String &&
                                   isDel.GetString()?.ToLowerInvariant() == "true")),
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
                                 name = item.TryGetProperty("name", out var iname) ? iname.GetString() : null,
                                 modifiers = item.TryGetProperty("modifiers", out var imod) ? imod.GetString() : null,
                                 quantity = item.TryGetProperty("quantity", out var iqty) && iqty.TryGetInt32(out var qv) ? qv : 1,
                                 price = item.TryGetProperty("price", out var iprice) && iprice.TryGetDecimal(out var pd) ? pd : 0m
                             }).ToArray()
                             : Array.Empty<object>(),
                ["subtotal"] = root.TryGetProperty("basket", out var bEl) && bEl.ValueKind == JsonValueKind.Array
                               ? bEl.EnumerateArray().Sum(x =>
                                   (x.TryGetProperty("price", out var pr) && pr.TryGetDecimal(out var pd) ? pd : 0m) *
                                   (x.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qv) ? qv : 1))
                               : 0m,
                ["deliveryFee"] = 0m,
                ["total"] = root.TryGetProperty("total", out var tot) && tot.TryGetDecimal(out var totalVal) ? totalVal : 0m
            };

            var jsonPayload = JsonSerializer.SerializeToElement(transformed);
            await email.SendSimpleInvoiceAsync(
                to: to,
                subject: subject,
                payload: jsonPayload,
                fromName: shopName,
                fromEmail: defaultFromEmail,
                ct: ct
            );

            var ops = Environment.GetEnvironmentVariable("BREVO_BCC");
            if (!string.IsNullOrWhiteSpace(ops))
            {
                await email.SendSimpleInvoiceAsync(
                    to: ops!,
                    subject: $"[Copy] {subject}",
                    payload: jsonPayload,
                    fromName: shopName,
                    fromEmail: defaultFromEmail,
                    ct: ct
                );
            }
        }
    }
}