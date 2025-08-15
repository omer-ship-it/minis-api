using System.Web;

namespace Minis.Services
{
    public sealed class NotificationContext
    {
        public int NewStatus { get; set; }
        public string? DriverName { get; set; }
        public string? DriverPhone { get; set; }
        public string? PickupEta { get; set; }
        public string? DeliveryEta { get; set; }
    }

    public interface INotificationService
    {
        Task MaybeNotifyAsync(OrderRow order, NotificationContext ctx, CancellationToken ct);
    }

    public sealed class NotificationService : INotificationService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<NotificationService> _log;

        public NotificationService(IHttpClientFactory httpFactory, ILogger<NotificationService> log)
        {
            _httpFactory = httpFactory;
            _log = log;
        }

        public async Task MaybeNotifyAsync(OrderRow order, NotificationContext ctx, CancellationToken ct)
        {
            // Email + WhatsApp logic based on status (2,4,5)
            var http = _httpFactory.CreateClient();

            string? emailUrl = null;
            string? whatsappText = null;

            if (ctx.NewStatus == 2)
            {
                whatsappText = $"{order.Id} allocated";
                if (!string.IsNullOrWhiteSpace(ctx.PickupEta))
                    whatsappText += $"\nPickup ETA: {ctx.PickupEta}";
            }
            else if (ctx.NewStatus == 4 && !order.EmailSentForOnTheWay)
            {
                emailUrl = $"https://studionative.io/tools/sendEmail?status=1&id={order.Id}&orderId={order.Id}&shop={HttpUtility.UrlEncode(order.Shop)}";
                whatsappText = $"{order.Id} on the way";
                await MarkOnTheWayAsync(order, ct);
            }
            else if (ctx.NewStatus == 5 && !order.EmailSentForDelivered)
            {
                // If/when you re-enable delivered email:
                // emailUrl = $"https://studionative.io/tools/sendEmail?status=2&id={order.Id}&orderId={order.Id}&shop={HttpUtility.UrlEncode(order.Shop)}";
                // whatsappText = $"{order.Id} delivered";
                await MarkDeliveredAsync(order, ct);
            }

            // Build time info (roughly same behaviour as old code)
            var timeInfo = BuildTimeInfo(order.LastModified, ctx.DeliveryEta, ctx.NewStatus);

            if (!string.IsNullOrWhiteSpace(emailUrl))
            {
                try
                {
                    await http.GetAsync(emailUrl, ct);
                    _log.LogInformation("Email notification sent for order {id}", order.Id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Email send failed for order {id}", order.Id);
                }
            }

            if (!string.IsNullOrWhiteSpace(whatsappText))
            {
                var msg = $"{whatsappText}{timeInfo}\n{ctx.DriverName} {ctx.DriverPhone}";
                var url = $"https://minitel.co.uk/utilities/whatsapp?type=text&group=Minitel&text={Uri.EscapeDataString(msg)}";
                try
                {
                    await http.GetAsync(url, ct);
                    _log.LogInformation("WhatsApp notification sent for order {id}", order.Id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "WhatsApp send failed for order {id}", order.Id);
                }
            }
        }

        private static string BuildTimeInfo(DateTime orderedUtc, string? deliveryEta, int status)
        {
            string suffix = "";
            try
            {
                var nowUtc = DateTime.UtcNow;
                var ordered = DateTime.SpecifyKind(orderedUtc, DateTimeKind.Utc);

                DateTime? eta = null;
                if (!string.IsNullOrWhiteSpace(deliveryEta) && DateTime.TryParse(deliveryEta, out var tmp))
                    eta = DateTime.SpecifyKind(tmp, DateTimeKind.Utc);

                if (status < 5 && eta.HasValue)
                {
                    var mins = (int)Math.Round((eta.Value - nowUtc).TotalMinutes);
                    suffix = mins < 60 ? $"\nDelivery ETA {mins} min" : $"\nDelivery ETA {eta.Value:HH:mm}";
                }
                else if (status == 5)
                {
                    var mins = (int)Math.Round((ordered - nowUtc).TotalMinutes);
                    suffix = mins < 60 ? $"\nJob time In {mins} min" : $"\nJob time {ordered:HH:mm}";
                }
            }
            catch
            {
                // ignore formatting errors
            }
            return suffix;
        }

        private async Task MarkOnTheWayAsync(OrderRow order, CancellationToken ct)
        {
            // This is intentionally left for the repository
            // but called here to keep the business flow in one place.
            // If you prefer, move both "mark" methods into repo and call them from the endpoint.
            // For now, do nothing here; endpoint/repo already handle flags after sending.
        }

        private async Task MarkDeliveredAsync(OrderRow order, CancellationToken ct)
        {
            // Same note as above; keep repo as the source of truth for flags.
        }
    }
}
