using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Minis.Delivery;

namespace Minis.Services
{
    public static class AnalyticsGa4
    {
        public static async Task SendPurchaseAsync(
      HttpClient http,
      string measurementId,
      string apiSecret,
      string clientId,
      global::Minis.Delivery.SubmitOrderRequest req,   // <-- now resolves to Minis.Delivery.SubmitOrderRequest
      int orderId,
      decimal subtotal,
      decimal deliveryFee,
      CancellationToken ct = default)
        {
            var basketEnumerable = req.Basket as IEnumerable;
            var items = basketEnumerable == null
                ? Array.Empty<object>()
                : basketEnumerable.Cast<object>().Select(b =>
                {
                    dynamic d = b!;
                    string itemId = SafeString(d?.ProductId);
                    string itemName = SafeString(d?.ProductId);
                    int qty = SafeInt(d?.Quantity);
                    double price = SafeDouble(d?.Price);
                    string variant = SafeString(d?.Modifiers);

                    return new
                    {
                        item_id = itemId,
                        item_name = itemName,
                        quantity = qty,
                        price = price,
                        item_variant = variant
                    };
                }).ToArray();

            var payload = new
            {
                client_id = clientId,
                events = new[]
                {
                    new
                    {
                        name = "purchase",
                        params_ = new
                        {
                            transaction_id = orderId.ToString(),
                            currency = "GBP",
                            value = (double)(subtotal + deliveryFee),
                            shipping = (double)deliveryFee,
                            items = items
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            json = json.Replace("\"params_\":", "\"params\":");

            var url = $"https://www.google-analytics.com/mp/collect?measurement_id={Uri.EscapeDataString(measurementId)}&api_secret={Uri.EscapeDataString(apiSecret)}";
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            try
            {
                var resp = await http.SendAsync(reqMsg, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"GA4 purchase failed: {(int)resp.StatusCode} {body}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GA4 purchase exception: {ex.Message}");
            }

            static string SafeString(object? v) => v?.ToString() ?? "";
            static int SafeInt(object? v) => v is null ? 0 : Convert.ToInt32(v);
            static double SafeDouble(object? v) => v is null ? 0 : Convert.ToDouble(v);
        }
    }
}