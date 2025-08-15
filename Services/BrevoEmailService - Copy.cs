/*
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Stripe;
using System.Linq;

namespace Minis.Services;

public interface IEmailService
{
    Task SendOrderEmailsAsync(
        int orderId,
        SubmitOrderRequest request,
        Charge charge,
        CancellationToken ct = default);

    // ✅ Expose debug helper so callers don’t need to cast
    Task<object> SendOrderEmailsAsyncWithResult(
        int orderId,
        SubmitOrderRequest request,
        Charge charge,
        CancellationToken ct = default);
}

public sealed class BrevoEmailService(HttpClient http, IConfiguration config) : IEmailService
{
    private readonly HttpClient _http = http;
    private readonly IConfiguration _config = config;

    // ---------- Models used for rendering ----------
    private sealed class BasketLine
    {
        public string Name { get; init; } = "Item";
        public string? Modifiers { get; init; }
        public decimal UnitPrice { get; init; }
        public int Quantity { get; init; } = 1;
        public decimal LineTotal => UnitPrice * Quantity;
    }

    private sealed class InvoiceParts
    {
        public List<BasketLine> Lines { get; } = new();
        public decimal Subtotal => Lines.Sum(l => l.LineTotal);
        public decimal DeliveryFee { get; set; }
        public decimal Total => Subtotal + DeliveryFee;

        public string? Address { get; set; }
        public string? Notes { get; set; }
        public string? When { get; set; }
    }

    // ---------- JSON helpers ----------
    private static decimal ReadDecimal(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(el.GetString(), out var d) ? d : 0m,
            _ => 0m
        };

    private static string? JoinAdditions(JsonElement additions)
    {
        if (additions.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var a in additions.EnumerateArray())
        {
            if (a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                names.Add(n.GetString()!);
        }
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static (string? name, decimal price) ReadOption(JsonElement opt)
    {
        string? name = null;
        decimal price = 0m;
        if (opt.ValueKind == JsonValueKind.Object)
        {
            if (opt.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString();
            if (opt.TryGetProperty("price", out var p))
                price = ReadDecimal(p);
        }
        return (name, price);
    }

    // ---------- Extract invoice (robust to many shapes) ----------
    private static InvoiceParts ExtractInvoice(SubmitOrderRequest req)
    {
        var parts = new InvoiceParts();

        // --- Basket ---
        try
        {
            var basketJson = JsonSerializer.Serialize(req.Basket);
            using var bdoc = JsonDocument.Parse(basketJson);

            if (bdoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in bdoc.RootElement.EnumerateArray())
                {
                    // Name from several possible fields
                    string name =
                        (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) ? n.GetString()! :
                        (el.TryGetProperty("productName", out var pn) && pn.ValueKind == JsonValueKind.String) ? pn.GetString()! :
                        (el.TryGetProperty("squareName", out var sn) && sn.ValueKind == JsonValueKind.String) ? sn.GetString()! :
                        "Item";

                    // Base price (string/number)
                    decimal unit = 0m;
                    if (el.TryGetProperty("price", out var p)) unit = ReadDecimal(p);

                    // Option (optional)
                    string? optionName = null; decimal optionPrice = 0m;
                    if (el.TryGetProperty("selectedOption", out var opt))
                    {
                        var tup = ReadOption(opt);
                        optionName = tup.name;
                        optionPrice = tup.price;
                    }

                    // Additions (optional)
                    string? additionsList = null; decimal additionsTotal = 0m;
                    if (el.TryGetProperty("selectedAdditions", out var adds) && adds.ValueKind == JsonValueKind.Array)
                    {
                        additionsList = JoinAdditions(adds);
                        foreach (var a in adds.EnumerateArray())
                        {
                            if (a.TryGetProperty("price", out var ap))
                                additionsTotal += ReadDecimal(ap);
                        }
                    }

                    // Modifiers might be string or array of strings
                    string? mods = null;
                    if (el.TryGetProperty("modifiers", out var m))
                    {
                        if (m.ValueKind == JsonValueKind.String)
                            mods = m.GetString();
                        else if (m.ValueKind == JsonValueKind.Array)
                            mods = string.Join(", ",
                                m.EnumerateArray()
                                 .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                                 .Where(s => !string.IsNullOrWhiteSpace(s))!);
                    }

                    // Build final modifiers label
                    var labels = new List<string>();
                    if (!string.IsNullOrWhiteSpace(optionName)) labels.Add(optionName!);
                    if (!string.IsNullOrWhiteSpace(additionsList)) labels.Add(additionsList!);
                    if (!string.IsNullOrWhiteSpace(mods)) labels.Add(mods!);
                    var finalMods = labels.Count > 0 ? string.Join(" • ", labels) : null;

                    // Quantity
                    int qty = 1;
                    if (el.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
                        qty = q.TryGetInt32(out var i) ? i : 1;

                    // Effective unit
                    var finalUnit = unit + optionPrice + additionsTotal;

                    parts.Lines.Add(new BasketLine
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "Item" : name,
                        Modifiers = string.IsNullOrWhiteSpace(finalMods) ? null : finalMods,
                        UnitPrice = finalUnit,
                        Quantity = Math.Max(1, qty)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Invoice parse (basket) failed: " + ex.Message);
        }

        // --- Delivery ---
        decimal explicitDelivery = 0m;
        string? address = null;
        string? notes = null;
        string? when = null;

        try
        {
            var delJson = JsonSerializer.Serialize(req.Delivery);
            using var ddoc = JsonDocument.Parse(delJson);

            if (ddoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (ddoc.RootElement.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String)
                    address = a.GetString();

                if (ddoc.RootElement.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String)
                    notes = n.GetString();

                if (ddoc.RootElement.TryGetProperty("scheduledFor", out var s) && s.ValueKind == JsonValueKind.String)
                    when = s.GetString();

                // multiple possible keys for delivery fee
                if (ddoc.RootElement.TryGetProperty("deliveryFee", out var df))
                    explicitDelivery = ReadDecimal(df);
                else if (ddoc.RootElement.TryGetProperty("fee", out var f))
                    explicitDelivery = ReadDecimal(f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Invoice parse (delivery) failed: " + ex.Message);
        }

        // Infer delivery if not explicit (Total – Subtotal)
        var inferredDelivery = Math.Max(0m, req.Total - parts.Subtotal);

        parts.DeliveryFee = explicitDelivery > 0 ? explicitDelivery : inferredDelivery;
        parts.Address = address;
        parts.Notes = notes;
        parts.When = string.IsNullOrWhiteSpace(when) ? "asap" : when;

        return parts;
    }

    // ---------- Public send with result (handy for debugging) ----------
    public async Task<object> SendOrderEmailsAsyncWithResult(
       int orderId, SubmitOrderRequest request, Charge charge, CancellationToken ct = default)
    {
        var (toEmail, toName) = await GetCustomerByOrderAsync(orderId, ct)
            ?? (request.Email ?? "orders@minis.app", request.Name ?? "Customer");

        var (senderName, senderEmail) = GetSenderForShop(request.MiniAppId.ToString());
        senderEmail = _config["Brevo:DefaultSenderEmail"] ?? senderEmail;

        // --- DEBUG: log raw payloads as seen server-side
        string basketJsonSafe = SafeSerialize(request.Basket);
        string deliveryJsonSafe = SafeSerialize(request.Delivery);
        Console.WriteLine("📦 basket json: " + basketJsonSafe);
        Console.WriteLine("🚚 delivery json: " + deliveryJsonSafe);

        string html;
        string text;

        try
        {
            // Try the rich invoice first
            html = BuildInvoiceHtml(orderId, request.MiniAppId.ToString(), toName, request);
            text = BuildTextContent(orderId, request.MiniAppId.ToString(), toName, request);
        }
        catch (Exception ex)
        {
            // Fallback to a simple, robust email body if parsing fails
            Console.WriteLine("❌ Invoice build failed: " + ex);
            html = BuildMinimalHtml(orderId, request, toName, basketJsonSafe, deliveryJsonSafe);
            text = $"Order #{orderId}\nTotal: £{request.Total:0.00}\n(basket/delivery parsing failed, sent minimal email)";
        }

        var payload = new
        {
            sender = new { name = senderName, email = senderEmail },
            to = new[] { new { email = toEmail, name = toName } },
            bcc = new[] { new { email = _config["Brevo:BccEmail"] ?? "ops@minis.app", name = "Ops" } },
            subject = $"Your Invoice for Order #{orderId}",
            htmlContent = html,
            textContent = text
        };

        var apiKey = _config["Brevo:ApiKey"] ?? throw new InvalidOperationException("Missing Brevo:ApiKey");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        req.Headers.Add("api-key", apiKey);
        req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            Console.WriteLine($"❌ Brevo API error: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

        return new
        {
            status = (int)resp.StatusCode,
            reason = resp.ReasonPhrase,
            requestSender = senderEmail,
            requestTo = toEmail,
            brevoResponse = body
        };
    }

    private static string BuildMinimalHtml(int orderId, SubmitOrderRequest req, string name, string basketJson, string deliveryJson) => $@"
<!doctype html>
<html><body style='font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#111'>
  <h2 style='margin:0 0 8px'>Thanks, {System.Net.WebUtility.HtmlEncode(name)}!</h2>
  <div>Order <strong>#{orderId}</strong></div>
  <div>Total: <strong>£{req.Total:0.00}</strong></div>
  <hr />
  <div style='font-size:12px;color:#555'>Basket JSON:</div>
  <pre style='white-space:pre-wrap;font-size:12px;background:#f7f7f8;padding:8px;border-radius:8px'>{System.Net.WebUtility.HtmlEncode(basketJson)}</pre>
  <div style='font-size:12px;color:#555'>Delivery JSON:</div>
  <pre style='white-space:pre-wrap;font-size:12px;background:#f7f7f8;padding:8px;border-radius:8px'>{System.Net.WebUtility.HtmlEncode(deliveryJson)}</pre>
</body></html>";

    // Serialize without throwing (for logs)
    private static string SafeSerialize(object? o)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(o); }
        catch { return "<unserializable>"; }
    }

    private (string name, string email) GetSenderForShop(string shopId) =>
        (shopId is "1" or "9")
            ? ("Beigel Bake", "beigelbake@hoodapp.co.uk")
            : (_config["Brevo:DefaultSenderName"] ?? "Minis",
               _config["Brevo:DefaultSenderEmail"] ?? "orders@minis.app");

    private static string Money(decimal v) => $"£{v:0.00}";

    private static string BuildTextContent(int orderId, string shopId, string name, SubmitOrderRequest req)
    {
        var inv = ExtractInvoice(req);
        var sb = new StringBuilder();
        sb.AppendLine($"MINIS — Order #{orderId} (shop {shopId})");
        sb.AppendLine($"Hi {name},");
        sb.AppendLine();
        foreach (var l in inv.Lines)
        {
            sb.AppendLine($"{l.Name} {(string.IsNullOrWhiteSpace(l.Modifiers) ? "" : $"[{l.Modifiers}]")} — " +
                          $"{l.Quantity} × {Money(l.UnitPrice)} = {Money(l.LineTotal)}");
        }
        sb.AppendLine();
        sb.AppendLine($"Subtotal: {Money(inv.Subtotal)}");
        sb.AppendLine($"Delivery: {Money(inv.DeliveryFee)}");
        sb.AppendLine($"Total:    {Money(inv.Subtotal + inv.DeliveryFee)}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(inv.When)) sb.AppendLine($"When: {inv.When}");
        if (!string.IsNullOrWhiteSpace(inv.Address)) sb.AppendLine($"Address: {inv.Address}");
        if (!string.IsNullOrWhiteSpace(inv.Notes)) sb.AppendLine($"Notes: {inv.Notes}");
        sb.AppendLine();
        sb.AppendLine("Paid with Apple Pay (Stripe).");
        return sb.ToString();
    }

    private static string BuildInvoiceHtml(int orderId, string shopId, string name, SubmitOrderRequest req)
    {
        var inv = ExtractInvoice(req);

        var linesHtml = string.Join("", inv.Lines.Select(l => $@"
<tr>
  <td style='padding:10px 0;'>
    <div style=""font-weight:600"">{System.Net.WebUtility.HtmlEncode(l.Name)}</div>
    {(string.IsNullOrWhiteSpace(l.Modifiers) ? "" :
            $"<div style='color:#6b7280;font-size:12px;margin-top:2px;'>{System.Net.WebUtility.HtmlEncode(l.Modifiers!)}</div>")}
  </td>
  <td style='padding:10px 0;text-align:center;white-space:nowrap;'>{l.Quantity}</td>
  <td style='padding:10px 0;text-align:right;white-space:nowrap;'>{Money(l.UnitPrice)}</td>
  <td style='padding:10px 0;text-align:right;white-space:nowrap;font-weight:600'>{Money(l.LineTotal)}</td>
</tr>
<tr><td colspan='4' style='height:1px;background:#eee;'></td></tr>
"));

        var addressBlock = string.IsNullOrWhiteSpace(inv.Address) ? "" : $@"
  <div style='margin-top:8px;'><strong>Address:</strong> {System.Net.WebUtility.HtmlEncode(inv.Address!)}</div>";
        var notesBlock = string.IsNullOrWhiteSpace(inv.Notes) ? "" : $@"
  <div style='margin-top:6px;'><strong>Notes:</strong> {System.Net.WebUtility.HtmlEncode(inv.Notes!)}</div>";

        return $@"
<!doctype html>
<html>
  <head><meta charset='utf-8' /><meta name='viewport' content='width=device-width, initial-scale=1' /><title>Invoice #{orderId}</title></head>
  <body style='margin:0;background:#f7f7f8;padding:16px;font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Arial,sans-serif;color:#111827;'>
    <div style='max-width:680px;margin:0 auto;background:#fff;border:1px solid rgba(0,0,0,.06);border-radius:14px;overflow:hidden;'>
      <div style='padding:20px 24px;border-bottom:1px solid #eee;display:flex;align-items:center;justify-content:space-between;'>
        <div style='font-weight:800;font-size:18px;'>MINIS</div>
        <div style='color:#6b7280;font-size:12px;'>Order #{orderId} • Shop {shopId}</div>
      </div>

      <div style='padding:22px 24px;'>
        <div style='font-size:16px;font-weight:700;margin-bottom:8px;'>Thanks, {System.Net.WebUtility.HtmlEncode(name)}!</div>
        <div style='color:#6b7280;font-size:13px;'>Here is your receipt.</div>

        <div style='margin-top:16px;padding:12px 14px;background:#fafafa;border:1px solid #eee;border-radius:10px;'>
          <div><strong>When:</strong> {System.Net.WebUtility.HtmlEncode(inv.When ?? "asap")}</div>
          {addressBlock}
          {notesBlock}
        </div>

        <table role='presentation' width='100%' cellpadding='0' cellspacing='0' style='margin-top:18px;border-collapse:collapse;'>
          <thead>
            <tr>
              <th align='left' style='padding:8px 0;color:#6b7280;font-weight:600;text-transform:uppercase;font-size:12px;'>Item</th>
              <th align='center' style='padding:8px 0;color:#6b7280;font-weight:600;text-transform:uppercase;font-size:12px;'>Qty</th>
              <th align='right' style='padding:8px 0;color:#6b7280;font-weight:600;text-transform:uppercase;font-size:12px;'>Unit</th>
              <th align='right' style='padding:8px 0;color:#6b7280;font-weight:600;text-transform:uppercase;font-size:12px;'>Total</th>
            </tr>
            <tr><td colspan='4' style='height:1px;background:#eee;'></td></tr>
          </thead>
          <tbody>
            {linesHtml}
          </tbody>
        </table>

        <div style='margin-top:14px;border-top:1px dashed #e5e7eb;padding-top:12px;'>
          <div style='display:flex;justify-content:space-between;margin:6px 0;'>
            <div style='color:#6b7280;'>Subtotal</div><div>{Money(inv.Subtotal)}</div>
          </div>
          <div style='display:flex;justify-content:space-between;margin:6px 0;'>
            <div style='color:#6b7280;'>Delivery</div><div>{Money(inv.DeliveryFee)}</div>
          </div>
          <div style='height:1px;background:#eee;margin:10px 0;'></div>
          <div style='display:flex;justify-content:space-between;margin:6px 0;font-weight:800;font-size:16px;'>
            <div>Total</div><div>{Money(inv.Subtotal + inv.DeliveryFee)}</div>
          </div>
        </div>
      </div>

      <div style='padding:16px 24px;background:#fafafa;border-top:1px solid #eee;color:#6b7280;font-size:12px;'>
        Paid with Apple Pay (Stripe). Thanks for ordering with Minis!
      </div>
    </div>
  </body>
</html>";
    }

    // ---------- DB lookup ----------
    private async Task<(string email, string name)?> GetCustomerByOrderAsync(int orderId, CancellationToken ct)
    {
        var cs = _config.GetConnectionString("DefaultConnection");
        const string sql = @"
SELECT TOP 1 c.Email, c.Name
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
WHERE o.Id = @OrderId";

        await using var conn = new SqlConnection(cs);
        var row = await conn.QueryFirstOrDefaultAsync<(string Email, string Name)>(
            new CommandDefinition(sql, new { OrderId = orderId }, cancellationToken: ct));

        return row.Email is not null
            ? (row.Email, string.IsNullOrWhiteSpace(row.Name) ? "Customer" : row.Name)
            : null;
    }

    // ---------- Public interface ----------
    public async Task SendOrderEmailsAsync(
        int orderId,
        SubmitOrderRequest request,
        Charge charge,
        CancellationToken ct = default)
    {
        var result = await SendOrderEmailsAsyncWithResult(orderId, request, charge, ct);
        Console.WriteLine($"📧 Brevo send result: {JsonSerializer.Serialize(result)}");
    }
}

*/