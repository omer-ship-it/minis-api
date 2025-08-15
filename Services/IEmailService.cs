#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Minis.Services
{
    public interface IEmailService
    {
        Task SendRawHtmlAsync(
            string to,
            string subject,
            string htmlBody,
            string? fromName = null,
            string? fromEmail = null,
            string? bcc = null,
            CancellationToken ct = default);

        Task SendSimpleInvoiceAsync(
            string to,
            string subject,
            JsonElement payload,
            string? fromName = null,
            string? fromEmail = null,
            string? bcc = null,
            CancellationToken ct = default);
    }


    public sealed class BrevoEmailService : IEmailService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _defaultFromName;
        private readonly string _defaultFromEmail;

        public BrevoEmailService(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _apiKey = cfg["Brevo:ApiKey"] ?? throw new InvalidOperationException("Missing Brevo:ApiKey");
            _defaultFromName = cfg["Brevo:DefaultSenderName"] ?? "Minis";
            _defaultFromEmail = cfg["Brevo:DefaultSenderEmail"] ?? "orders@minis.app";
        }

        // --- Brevo models for tidy serialization ---
        private sealed class BrevoPerson
        {
            public string email { get; set; } = "";
            public string? name { get; set; }
        }
        private sealed class BrevoSender
        {
            public string email { get; set; } = "";
            public string name { get; set; } = "";
        }
        private sealed class BrevoPayload
        {
            public BrevoSender sender { get; set; } = default!;
            public BrevoPerson[] to { get; set; } = default!;
            public BrevoPerson[]? bcc { get; set; }
            public string subject { get; set; } = "";
            public string htmlContent { get; set; } = "";
        }

        public async Task SendRawHtmlAsync(
            string to,
            string subject,
            string htmlBody,
            string? fromName = null,
            string? fromEmail = null,
            string? bcc = null,
            CancellationToken ct = default)
        {
            var payload = new BrevoPayload
            {
                sender = new BrevoSender { name = fromName ?? _defaultFromName, email = fromEmail ?? _defaultFromEmail },
                to = new[] { new BrevoPerson { email = to } },
                bcc = string.IsNullOrWhiteSpace(bcc) ? null : new[] { new BrevoPerson { email = bcc! } },
                subject = subject,
                htmlContent = htmlBody
            };

            var opts = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            req.Headers.TryAddWithoutValidation("api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("accept", "application/json");
            req.Content = new StringContent(JsonSerializer.Serialize(payload, opts), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"⚠️ Brevo send failed: {(int)resp.StatusCode} {body}");
            }
        }

        // Simple invoice (classic layout) from a loose JSON payload
        public async Task SendSimpleInvoiceAsync(
            string to,
            string subject,
            JsonElement root,
            string? fromName = null,
            string? fromEmail = null,
            string? bcc = null,
            CancellationToken ct = default)
        {
            // helpers
            static string S(JsonElement e, string prop, string def = "") =>
                e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? def) : def;
            static decimal D(JsonElement e, string prop, decimal def = 0m) =>
                e.TryGetProperty(prop, out var p) && p.TryGetDecimal(out var v) ? v : def;
            static int I(JsonElement e, string prop, int def = 0) =>
                e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : def;

            // basics
            var orderId = I(root, "orderId", new Random().Next(1000, 9999));
            var shopName = S(root, "shopName", "Beigel Bake");
            var logoUrl = S(root, "logoUrl", "https://www.beigelbake.co.uk/cdn/shop/files/main-logo.jpg?v=1672516746&width=400");
            var buttonHex = S(root, "buttonColor", "#c72d22");
            var whenText = S(root, "whenText", S(root, "scheduledFor", ""));
            var trackUrl = S(root, "trackUrl", $"https://hoodapp.co.uk/tracker.html?id={orderId}");

            // customer / delivery
            var customer = root.TryGetProperty("customer", out var cust) ? cust : root;
            var customerName = S(customer, "name", "Customer");
            var phone = S(customer, "phone", "");

            var delivery = root.TryGetProperty("delivery", out var del) ? del : root;
            var isDelivery = delivery.TryGetProperty("isDelivery", out var d)
                             ? (d.ValueKind == JsonValueKind.True ||
                                (d.ValueKind == JsonValueKind.String && (d.GetString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase)))
                             : true;

            var addr = delivery.TryGetProperty("address", out var a) ? a
                     : root.TryGetProperty("address", out var a2) ? a2
                     : default;

            string? address1 = addr.ValueKind != JsonValueKind.Undefined ? S(addr, "address1") : S(root, "address1");
            string? address2 = addr.ValueKind != JsonValueKind.Undefined ? S(addr, "address2") : S(root, "address2");
            string? postcode = addr.ValueKind != JsonValueKind.Undefined ? S(addr, "postcode") : S(root, "postcode");
            if (string.IsNullOrWhiteSpace(phone))
                phone = addr.ValueKind != JsonValueKind.Undefined ? S(addr, "phone", "") : phone;

            // lines
            var itemsEl = root.TryGetProperty("basket", out var bEl) && bEl.ValueKind == JsonValueKind.Array ? bEl
                        : root.TryGetProperty("items", out var iEl) && iEl.ValueKind == JsonValueKind.Array ? iEl
                        : default;

            var lines = new List<(string name, string? modifiers, int qty, decimal unitPrice)>();
            if (itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in itemsEl.EnumerateArray())
                {
                    var name = S(it, "name", "Item");
                    var mods = S(it, "modifiers", "");
                    var qty = I(it, "quantity", 1);
                    var unit = D(it, "price", D(it, "unitPrice", 0m));
                    lines.Add((name, string.IsNullOrWhiteSpace(mods) ? null : mods, qty, unit));
                }
            }

            // totals
            var providedSubtotal = D(root, "subtotal", 0m);
            var subtotal = providedSubtotal > 0 ? providedSubtotal : lines.Sum(x => x.unitPrice * x.qty);
            var total = D(root, "total", subtotal);
            var deliveryFee = D(root, "deliveryFee", Math.Max(0, total - subtotal));
            var discount = D(root, "discount", 0m);

            // html
            var html = BuildClassicInvoiceHtml(
                orderId: orderId,
                shopName: shopName,
                logoUrl: logoUrl,
                buttonColorHex: buttonHex,
                customerName: customerName,
                isDelivery: isDelivery,
                address1: address1,
                address2: address2,
                postcode: postcode,
                phone: phone,
                whenText: string.IsNullOrWhiteSpace(whenText)
    ? null
    : (DateTime.TryParse(whenText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
        ? dt.ToString("ddd dd/MM 'at' HH:mm", CultureInfo.InvariantCulture)
        : whenText),
                lines: lines,
                subtotal: subtotal,
                deliveryFee: deliveryFee,
                discount: discount,
                total: total,
                trackUrl: trackUrl
            );

            await SendRawHtmlAsync(
                to: to,
                subject: subject,
                htmlBody: html,
                fromName: fromName,
                fromEmail: fromEmail,
                bcc: bcc,                          // e.g. "eilamomer@gmail.com"
                ct: ct
            );
        }

        // Classic ASPX-style HTML layout
        private static string BuildClassicInvoiceHtml(
            int orderId,
            string shopName,
            string? logoUrl,
            string buttonColorHex, // kept for compatibility; CTA uses #c72d22
            string customerName,
            bool isDelivery,
            string? address1,
            string? address2,
            string? postcode,
            string? phone,
            string? whenText,
            IEnumerable<(string name, string? modifiers, int qty, decimal unitPrice)> lines,
            decimal subtotal,
            decimal deliveryFee,
            decimal discount,
            decimal total,
            string trackUrl)
        {
            string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            string Money(decimal d) => "£" + d.ToString("0.00", CultureInfo.GetCultureInfo("en-GB"));

            var sb = new StringBuilder();
            sb.Append($@"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Invoice</title>
<style>
  body{{margin:0;background:#f6f7fb;color:#111;font:14px/1.5 -apple-system,BlinkMacSystemFont,""Segoe UI"",Roboto,Helvetica,Arial,sans-serif}}
  .wrap{{max-width:680px;margin:0 auto;padding:20px}}
  .card{{background:#fff;border-radius:12px;padding:18px;box-shadow:0 1px 2px rgba(0,0,0,.05)}}
  .muted{{color:#6b7280}}
  .hr{{height:1px;background:#eee;margin:14px 0}}
  table{{width:100%;border-collapse:collapse}}
  td{{padding:8px 0;vertical-align:top}}
  .qty{{width:50px}}
  .right{{text-align:right; white-space:nowrap}}
  .total{{font-weight:800}}
  .btn{{display:inline-block;padding:12px 16px;border-radius:10px;text-decoration:none}}
</style>
</head>
<body>
  <div class=""wrap"">

    <!-- Header with centered logo -->
    <div class=""card"" style=""text-align:center; margin-bottom:12px"">");

            if (!string.IsNullOrWhiteSpace(logoUrl))
                sb.Append($@"<img src=""{H(logoUrl)}"" width=""165"" alt=""{H(shopName)}"" style=""display:inline-block"">");
            else
                sb.Append($@"<div style=""font-size:22px;font-weight:800"">{H(shopName)}</div>");

            sb.Append(@"
    </div>

    <!-- Total row -->
    <div class=""card"" style=""margin-bottom:12px; padding-top:18px; padding-bottom:10px"">
      <table style=""width:100%"">
        <tr>
          <td style=""font-size:28px;font-weight:800; padding:0"">Total</td>
          <td class=""right"" style=""font-size:28px;font-weight:800; padding:0"">" + Money(total) + @"</td>
        </tr>
      </table>
    </div>

    <!-- Customer / Delivery block -->
    <div class=""card"" style=""margin-bottom:12px"">");

            if (!string.IsNullOrWhiteSpace(customerName))
                sb.Append($@"<div style=""font-weight:600"">{H(customerName)}</div>");

            if (isDelivery)
            {
                if (!string.IsNullOrWhiteSpace(address1)) sb.Append($@"<div class=""muted"">{H(address1)}</div>");
                if (!string.IsNullOrWhiteSpace(address2)) sb.Append($@"<div class=""muted"">{H(address2)}</div>");
                if (!string.IsNullOrWhiteSpace(postcode)) sb.Append($@"<div class=""muted"">{H(postcode)}</div>");
                if (!string.IsNullOrWhiteSpace(phone)) sb.Append($@"<div class=""muted"">{H(phone)}</div>");
            }
            else
            {
                sb.Append(@"<div class=""muted"">Collection order</div>");
            }

            if (!string.IsNullOrWhiteSpace(whenText))
                sb.Append($@"<div class=""muted"" style=""margin-top:6px"">When: {H(whenText)}</div>");

            sb.Append(@"
    </div>

    <!-- Items table -->
    <div class=""card"" style=""margin-bottom:12px"">
      <table>");

            foreach (var line in lines)
            {
                var lineTotal = line.unitPrice * line.qty;
                sb.Append($@"
        <tr>
          <td class=""qty"">{line.qty}x</td>
          <td>
            {H(line.name)}
            {(string.IsNullOrWhiteSpace(line.modifiers) ? "" : $@"<div class=""muted"" style=""font-size:12px"">• {H(line.modifiers)}</div>")}
          </td>
          <td class=""right"">{Money(lineTotal)}</td>
        </tr>");
            }

            sb.Append($@"
      </table>

      <div class=""hr""></div>

      <table>
        <tr>
          <td class=""muted"">Subtotal</td>
          <td class=""right"">{Money(subtotal)}</td>
        </tr>");

            if (isDelivery)
                sb.Append($@"<tr><td class=""muted"">Delivery</td><td class=""right"">{(deliveryFee <= 0 ? "Free" : Money(deliveryFee))}</td></tr>");

            if (discount > 0)
                sb.Append($@"<tr><td class=""muted"">Discount</td><td class=""right"">-{Money(discount)}</td></tr>");

            sb.Append(@"
      </table>
    </div>

    <!-- CTA -->
    <div class=""card"" style=""text-align:center;margin-bottom:12px"">
      <a href=""" + H(trackUrl) + @""" 
         class=""btn"" 
         style=""background:#c72d22;color:#ffffff !important;text-decoration:none !important;display:inline-block"">Track your order</a>
    </div>

    <div class=""muted"" style=""text-align:center;font-size:12px"">
      This is an automated email from " + H(shopName) + @". For help, reply to this message.
    </div>

  </div>
</body>
</html>");

            return sb.ToString();
        }
    }
}