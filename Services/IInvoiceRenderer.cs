#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Globalization;
using Minis.Delivery;

namespace Minis.Services
{
    public interface IInvoiceRenderer
    {
        string RenderHtml(InvoiceModel m);
    }

    public sealed class InvoiceRenderer : IInvoiceRenderer
    {
        public string RenderHtml(InvoiceModel m)
        {
            var sb = new StringBuilder();

            string money(decimal d) => "£" + d.ToString("0.00", CultureInfo.GetCultureInfo("en-GB"));

            sb.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Invoice</title>
<style>
  body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;margin:0;padding:0;background:#f6f7fb;color:#111}
  .wrap{max-width:640px;margin:0 auto;padding:24px}
  .card{background:#fff;border-radius:12px;padding:20px;box-shadow:0 1px 2px rgba(0,0,0,.05)}
  .muted{color:#6b7280}
  .row{display:flex;justify-content:space-between;align-items:center}
  .hr{height:1px;background:#eee;margin:14px 0}
  .btn{display:inline-block;padding:12px 16px;border-radius:10px;text-decoration:none;color:#fff}
  table{width:100%;border-collapse:collapse}
  td{padding:8px 0;vertical-align:top}
  .right{text-align:right}
  .total{font-weight:600}
</style>
</head>
<body>
  <div class="wrap">
    <div class="card">
""");

            // Header / logo or shop name
            if (!string.IsNullOrWhiteSpace(m.ShopLogoUrl))
                sb.Append($"<div style='margin-bottom:10px'><img src='{m.ShopLogoUrl}' alt='Logo' width='150' /></div>");
            else
                sb.Append($"<h2 style='margin:0 0 8px 0'>{m.ShopName}</h2>");

            sb.Append($"""
  <div class="muted" style="margin-bottom:18px">Order #{m.OrderId}</div>

  <div class="row" style="gap:12px;align-items:flex-start">
    <div>
      <div style="font-weight:600;margin-bottom:4px">{m.CustomerName}</div>
""");

            if (m.IsDelivery)
            {
                sb.Append($"<div class='muted'>{m.Address1}</div>");
                if (!string.IsNullOrWhiteSpace(m.Address2))
                    sb.Append($"<div class='muted'>{m.Address2}</div>");
                sb.Append($"<div class='muted'>{m.Postcode}</div>");
                if (!string.IsNullOrWhiteSpace(m.Phone))
                    sb.Append($"<div class='muted'>📞 {m.Phone}</div>");
            }
            else
            {
                sb.Append("<div class='muted'>Collection</div>");
            }

            if (!string.IsNullOrWhiteSpace(m.ScheduledForText))
                sb.Append($"<div class='muted' style='margin-top:6px'>When: {m.ScheduledForText}</div>");

            sb.Append("""
    </div>
  </div>

  <div class="hr"></div>

  <table>
""");

            foreach (var line in m.Lines)
            {
                sb.Append("<tr>");
                sb.Append($"<td style='width:48px'>{line.Quantity}×</td>");
                sb.Append("<td>");
                sb.Append($"{System.Net.WebUtility.HtmlEncode(line.Name)}");
                if (!string.IsNullOrWhiteSpace(line.Modifiers))
                    sb.Append($"<div class='muted' style='font-size:12px'>• {System.Net.WebUtility.HtmlEncode(line.Modifiers)}</div>");
                sb.Append("</td>");
                sb.Append($"<td class='right'>{money(line.LineTotal)}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</table>");
            sb.Append("<div class='hr'></div>");

            // Subtotal / delivery / discount
            sb.Append($"""
  <div class="row"><div class="muted">Items</div><div>{money(m.Subtotal)}</div></div>
""");

            if (m.IsDelivery)
            {
                sb.Append("<div class='row'>");
                sb.Append("<div class='muted'>Delivery</div>");
                sb.Append($"<div>{(m.DeliveryFee <= 0 ? "Free" : money(m.DeliveryFee))}</div>");
                sb.Append("</div>");
            }

            if (m.Discount > 0)
            {
                sb.Append($"""
<div class="row"><div class="muted">Discount</div><div>-{money(m.Discount)}</div></div>
""");
            }

            sb.Append("<div class='hr'></div>");
            sb.Append($"""
  <div class="row total"><div>Total</div><div>{money(m.Total)}</div></div>
""");

            if (!string.IsNullOrWhiteSpace(m.TrackUrl))
            {
                var btnColor = string.IsNullOrWhiteSpace(m.ButtonColorHex) ? "#1f4d46" : m.ButtonColorHex!;
                sb.Append($"""
  <div style="margin-top:18px">
    <a href="{m.TrackUrl}" class="btn" style="background:{btnColor}">Track your order</a>
  </div>
""");
            }

            sb.Append("""
    </div>
  </div>
</body>
</html>
""");

            return sb.ToString();
        }
    }

    // ===== Model you pass to the renderer =====
    public sealed class InvoiceModel
    {
        public int OrderId { get; init; }

        public string ShopName { get; init; } = "Minis";
        public string? ShopLogoUrl { get; init; }
        public string? ButtonColorHex { get; init; }

        public string CustomerName { get; init; } = "Customer";

        public bool IsDelivery { get; init; }
        public string? Address1 { get; init; }
        public string? Address2 { get; init; }
        public string? Postcode { get; init; }
        public string? Phone { get; init; }

        public string? ScheduledForText { get; init; } // e.g. "Wed 13/08 at 10:00"

        public decimal Subtotal { get; init; }
        public decimal DeliveryFee { get; init; }
        public decimal Discount { get; init; }
        public decimal Total { get; init; }

        public string? TrackUrl { get; init; }

        public Line[] Lines { get; init; } = Array.Empty<Line>();

        public sealed class Line
        {
            public string Name { get; init; } = "";
            public string? Modifiers { get; init; }
            public int Quantity { get; init; }
            public decimal UnitPrice { get; init; }
            public decimal LineTotal { get; init; }
        }
    }
}