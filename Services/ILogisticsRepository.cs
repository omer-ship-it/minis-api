// File: Delivery.cs
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Minis.Delivery // <— CHANGED NAMESPACE
{
    // ===== Incoming JSON (from your App Clip) =====
    public sealed class SubmitOrderRequest
    {
        public string UUID { get; set; } = default!;

        // Old/Charges path (optional when using PaymentIntents)
        public string? ApplePayToken { get; set; }

        // New/PaymentIntents path (optional when using Charges)
        public string? StripePaymentIntentId { get; set; }

        public string? Email { get; set; }
        public string? Name { get; set; }
        public int MiniAppId { get; set; }
        public decimal Total { get; set; }

        public List<BasketItem> Basket { get; set; } = new();
        public DeliveryPayload? Delivery { get; set; }
        public string? FcmToken { get; set; }
    }

    public class BasketItem
    {
        public int Quantity { get; set; }
        public string? SquareId { get; set; }
        public string? Modifiers { get; set; }
        public int ProductId { get; set; }
        public string? ProductName { get; set; } // if you added it
        public string? Name { get; set; }
        public decimal Price { get; set; }
    }

    public sealed class DeliveryPayload
    {
        public AddressPayload? Address { get; set; }
        public string? Notes { get; set; }
        public string? ScheduledFor { get; set; } // "asap" or ISO8601
        public string? PickupTime { get; set; }
        public bool IsDelivery { get; set; } = true;
    }

    public sealed class AddressPayload
    {
        public string? Name { get; set; }
        public string Address1 { get; set; } = default!;
        public string? Address2 { get; set; }
        public string Postcode { get; set; } = default!;
        public string Phone { get; set; } = default!;
        public double? Lat { get; set; }
        public double? Lng { get; set; }
        public string? Dir { get; set; }
    }

    // ===== Courier models (renamed) =====
    public sealed record DeliveryJobRequest(
     int OrderId,
     string PickupName, string PickupCompany, string PickupAddress, string PickupPostcode, string PickupPhone, string PickupEmail,
     double PickupLat, double PickupLng,
     string DropName, string DropCompany, string DropAddress1, string? DropAddress2, string DropPostcode, string DropPhone, string DropEmail,
     double DropLat, double DropLng,
     DateTimeOffset DeliveryTimeLondon,
     DateTimeOffset? PickupTimeLondon,
     string? DropInstructions,
     string Reference,
     bool IsUrgent // 👈 NEW
 );

    public sealed record DeliveryJobResult(string Provider, string DeliveryId);


    // ===== Mapper: JSON → DeliveryJobRequest (no DB read) =====
    public static class DeliveryMapper
    {
        public static DeliveryJobRequest BuildFromRequest(int orderId, SubmitOrderRequest req, IConfiguration cfg)
        {
            if (req.Delivery?.Address is null)
                throw new InvalidOperationException("Delivery address is required for dispatch.");

            var addr = req.Delivery.Address;

            string pickupName = cfg["Shop:Name"] ?? "Beigel Bake";
            string pickupCompany = cfg["Shop:Company"] ?? pickupName;
            string pickupAddress = cfg["Shop:Address"] ?? "159 Brick Lane";
            string pickupPostcode = cfg["Shop:Postcode"] ?? "E1 6SB";
            string pickupPhone = cfg["Shop:Phone"] ?? "+44 20 7729 0616";
            string pickupEmail = cfg["Shop:Email"] ?? "ops@beigelbake.co.uk";
            double pickupLat = ParseDoubleOr(cfg["Shop:Lat"], 51.5210);
            double pickupLng = ParseDoubleOr(cfg["Shop:Lng"], -0.0710);
            // …(unchanged shop defaults)… 

            // Resolve delivery (dropoff) time in London
            var deliveryWhenLondon = ResolveLondonWhen(req.Delivery!.ScheduledFor);

            // Try to use client-provided pickup; else fallback (e.g. 30 min before drop)
            var pickupWhenLondon = ResolveLondonOptional(req.Delivery!.PickupTime)
                                   ?? deliveryWhenLondon.AddMinutes(-30);

            // Urgent if explicit "asap" OR pickup is very soon
            var londonNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, GetLondonTz());
            bool isUrgent = (string.IsNullOrWhiteSpace(req.Delivery!.ScheduledFor) ||
                             req.Delivery!.ScheduledFor!.Equals("asap", StringComparison.OrdinalIgnoreCase))
                            || pickupWhenLondon <= londonNow.AddMinutes(10);

        
            string dropPhone = NormalizePhoneWithFallback(addr.Phone);

            return new DeliveryJobRequest(
                OrderId: orderId,
                PickupName: pickupName,
                PickupCompany: pickupCompany,
                PickupAddress: pickupAddress,
                PickupPostcode: pickupPostcode,
                PickupPhone: pickupPhone,
                PickupEmail: pickupEmail,
                PickupLat: pickupLat,
                PickupLng: pickupLng,

                DropName: addr.Name ?? req.Name ?? "Customer",
                DropCompany: "",
                DropAddress1: addr.Address1,
                DropAddress2: addr.Address2,
                DropPostcode: addr.Postcode,
                DropPhone: dropPhone,
                DropEmail: req.Email ?? "ops@example.com",
                DropLat: addr.Lat ?? 0,
                DropLng: addr.Lng ?? 0,

                DeliveryTimeLondon: deliveryWhenLondon,
                PickupTimeLondon: pickupWhenLondon, // 👈 NEW
                DropInstructions: req.Delivery!.Notes ?? addr.Dir,
                Reference: orderId.ToString(),
                IsUrgent: isUrgent
            );
        }

        private static DateTimeOffset? ResolveLondonOptional(string? timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr)) return null;

            var tz = GetLondonTz();

            if (DateTimeOffset.TryParse(timeStr, out var dto))
            {
                // If it looks "naive" (no offset/Z), treat as London local
                if (dto.Offset == TimeSpan.Zero &&
                    !timeStr.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
                    !timeStr.Contains('+'))
                {
                    var unspec = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
                    return new DateTimeOffset(unspec, tz.GetUtcOffset(unspec));
                }
                return TimeZoneInfo.ConvertTime(dto, tz);
            }

            return null;
        }
        private static double ParseDoubleOr(string? s, double fallback)
            => double.TryParse(s, out var v) ? v : fallback;

        private static TimeZoneInfo GetLondonTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        }

        public static DateTimeOffset ResolveLondonWhen(string? scheduledFor)
        {
            var tz = GetLondonTz();

            if (string.IsNullOrWhiteSpace(scheduledFor) ||
                scheduledFor.Equals("asap", StringComparison.OrdinalIgnoreCase))
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var londonNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
                return londonNow.AddMinutes(40);
            }

            if (DateTimeOffset.TryParse(scheduledFor, out var dto))
            {
                if (dto.Offset == TimeSpan.Zero &&
                    !scheduledFor.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
                    !scheduledFor.Contains('+'))
                {
                    var unspec = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
                    return new DateTimeOffset(unspec, tz.GetUtcOffset(unspec));
                }
                var londonLocal = TimeZoneInfo.ConvertTime(dto, tz);
                return londonLocal;
            }

            var nowUtc2 = DateTimeOffset.UtcNow;
            var londonNow2 = TimeZoneInfo.ConvertTime(nowUtc2, tz);
            return londonNow2.AddMinutes(40);
        }

        private static string NormalizePhoneWithFallback(string? raw)
        {
            const string fallbackPhone = "+447522552608";
            if (string.IsNullOrWhiteSpace(raw)) return fallbackPhone;

            raw = raw.Trim();

            // If it starts with a '+' and is NOT +44, keep as-is (assume user provided a valid international)
            if (raw.StartsWith("+") && !raw.StartsWith("+44"))
                return raw;

            // Strip non-digits for easier checks
            var digits = new string(raw.Where(char.IsDigit).ToArray());

            // 07xxxxxxxxx (11 digits) => +44 7xxxxxxxxx
            if (digits.StartsWith("07") && digits.Length == 11)
                return "+44" + digits[1..];

            // 44xxxxxxxxxx (12 digits) => +44xxxxxxxxxxx
            if (digits.StartsWith("44") && digits.Length == 12)
                return "+" + digits;

            // Already +44xxxxxxxxxxx?
            if (raw.StartsWith("+44") && digits.Length == 12)
                return raw;

            // Fallback if unknown/invalid pattern
            return fallbackPhone;
        }
    }

    // ===== Providers =====
    public interface IDeliveryProvider
    {
        Task<DeliveryJobResult> CreateAsync(DeliveryJobRequest req, CancellationToken ct);
        Task CancelAsync(string deliveryId, CancellationToken ct);
        string Name { get; }
    }

    public sealed class GophrProvider : IDeliveryProvider
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        public string Name => "gophr";

        public GophrProvider(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _apiKey = cfg["Gophr:ApiKey"] ?? throw new InvalidOperationException("Missing Gophr:ApiKey");
        }

        public async Task<DeliveryJobResult> CreateAsync(DeliveryJobRequest r, CancellationToken ct)
        {
            var form = new Dictionary<string, string?>
            {
                ["api_key"] = _apiKey,
                ["pickup_address1"] = r.PickupAddress,
                ["pickup_postcode"] = r.PickupPostcode,
                ["pickup_city"] = "London",
                ["pickup_company_name"] = r.PickupCompany,
                ["pickup_person_name"] = string.IsNullOrWhiteSpace(r.PickupName) ? "Counter" : r.PickupName,
                ["pickup_mobile_number"] = r.PickupPhone,

                ["delivery_address1"] = r.DropAddress1,
                ["delivery_address2"] = r.DropAddress2,
                ["delivery_postcode"] = r.DropPostcode,
                ["delivery_city"] = "London",
                ["delivery_mobile_number"] = r.DropPhone,
                ["delivery_person_name"] = string.IsNullOrWhiteSpace(r.DropName) ? "Customer" : r.DropName,

                ["reference_number"] = r.Reference,
                ["external_id"] = r.Reference,

                ["size_x"] = "10.0",
                ["size_y"] = "10.0",
                ["size_z"] = "10.0",
                ["weight"] = "0.5"
            };

            // 👇 For ASAP: omit earliest_pickup_time so Gophr treats it as an urgent job
            if (!r.IsUrgent)
            {
                var pick = (r.PickupTimeLondon ?? r.DeliveryTimeLondon.AddMinutes(-20))
                           .ToString("yyyy-MM-ddTHH:mm:sszzz");
                form["earliest_pickup_time"] = pick;
            }

            using var content = new FormUrlEncodedContent(form!);
            var resp = await _http.PostAsync("https://api.gophr.com/v1/commercial-api/create-confirm-job", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("data").GetProperty("job_id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("Gophr: missing job_id");
            return new DeliveryJobResult(Name, id!);
        }

        public Task CancelAsync(string deliveryId, CancellationToken ct) =>
            Task.CompletedTask; // implement if needed
    }

    public sealed class OrkestroProvider : IDeliveryProvider
    {
        private readonly HttpClient _http;
        public string Name => "orkestro";

        public OrkestroProvider(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            var apiKey = cfg["Orkestro:ApiKey"] ?? throw new InvalidOperationException("Missing Orkestro:ApiKey");
            _http.DefaultRequestHeaders.Remove("api-key");
            _http.DefaultRequestHeaders.Add("api-key", apiKey);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        public async Task<DeliveryJobResult> CreateAsync(DeliveryJobRequest r, CancellationToken ct)
        {
            var pickupStart = r.DeliveryTimeLondon.AddMinutes(-30).ToUniversalTime();
            var pickupEnd = pickupStart.AddMinutes(15);
            var dropStart = r.DeliveryTimeLondon.ToUniversalTime();
            var dropEnd = dropStart.AddMinutes(15);

            var payload = new
            {
                pickup = new
                {
                    name = r.PickupName,
                    companyName = r.PickupCompany,
                    addressLine1 = r.PickupAddress,
                    city = "London",
                    postCode = r.PickupPostcode,
                    country = "United Kingdom",
                    phone = r.PickupPhone,
                    email = r.PickupEmail,
                    instructions = "Pick up package from counter",
                    location = new { lat = r.PickupLat, @long = r.PickupLng }
                },
                dropoff = new
                {
                    name = r.DropName,
                    companyName = r.DropCompany,
                    addressLine1 = r.DropAddress1,
                    city = "London",
                    postCode = r.DropPostcode,
                    country = "United Kingdom",
                    phone = r.DropPhone,
                    email = r.DropEmail,
                    instructions = r.DropInstructions,
                    location = new { lat = r.DropLat, @long = r.DropLng }
                },
                parcel = new
                {
                    handlingInstructions = Array.Empty<string>(),
                    items = new[] { new { name = $"Order #{r.OrderId} Items" } },
                    reference = r.Reference,
                    value = new { amount = 18.20, currency = "gbp" } // TODO: set real amount if used
                },
                schedule = new
                {
                    type = "scheduled",
                    pickupWindow = new { start = pickupStart.ToString("yyyy-MM-ddTHH:mm:ssZ"), end = pickupEnd.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                    dropoffWindow = new { start = dropStart.ToString("yyyy-MM-ddTHH:mm:ssZ"), end = dropEnd.ToString("yyyy-MM-ddTHH:mm:ssZ") }
                },
                callbackUrl = "https://circuspizza.co.uk/webhooks/delivery-status",
                messagesCallbackUrl = "https://circuspizza.co.uk/webhooks/delivery-status"
            };

            var resp = await _http.PostAsJsonAsync("https://api.orkestro.io/orders", payload, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new Exception("Orkestro: missing id");
            return new DeliveryJobResult(Name, id!);
        }

        public async Task CancelAsync(string deliveryId, CancellationToken ct)
        {
            var resp = await _http.PostAsync($"https://api.orkestro.io/orders/{deliveryId}/cancel", content: null, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    // ===== Router =====
    public interface IDeliveryRouter
    {
        Task<DeliveryJobResult> DispatchAsync(DeliveryJobRequest request, CancellationToken ct);
    }



    public sealed class DeliveryRouter : IDeliveryRouter
    {
        private readonly IDeliveryProvider _gophr;
        private readonly IDeliveryProvider _orkestro;

        public DeliveryRouter(IEnumerable<IDeliveryProvider> providers)
        {
            _gophr = providers.Single(p => p.Name == "gophr");
            _orkestro = providers.Single(p => p.Name == "orkestro");
        }

        public Task<DeliveryJobResult> DispatchAsync(DeliveryJobRequest r, CancellationToken ct)
        {
            // Use pickup time when available; otherwise use delivery time
            var basis = r.PickupTimeLondon ?? r.DeliveryTimeLondon;

            // Decide by local London hour (r.* are already London-local DateTimeOffset)
            var hour = basis.Hour; // 0–23
            var useGophr = hour >= 7 && hour < 19; // 07:00–18:59 => Gophr, else Orkestro

            // Optional: log the decision for sanity
            Console.WriteLine($"[ROUTER] order={r.OrderId} basis={basis:yyyy-MM-dd HH:mm} hour={hour} => {(useGophr ? "gophr" : "orkestro")}");

            return useGophr ? _gophr.CreateAsync(r, ct) : _orkestro.CreateAsync(r, ct);
        }
    }
}