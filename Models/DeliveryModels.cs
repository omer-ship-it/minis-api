namespace Minis.Services;

// Keep it tiny; expand later as needed
public enum Courier { Uber, Gophr }
public record ApnsPushRequest(
    string Token,
    string Title,
    string Body,
    IDictionary<string, string>? Data
);
public sealed class ShopSettings
{
    public string? Name { get; set; }

    // Routing knobs
    public Courier? PreferredCourier { get; set; }  // hard override if set
    public bool UberEnabled { get; set; } = true;
    public bool GophrEnabled { get; set; } = true;

    // Day/Night defaults when no override
    public int DayStartsHour { get; set; } = 8;     // 08:00–19:59 => Gophr
    public int NightStartsHour { get; set; } = 20;  // 20:00–07:59 => Uber

    // Optional WhatsApp/ops group if you keep notifications
    public string? OpsGroup { get; set; }
}
public sealed record DeliveryRequest(
    int OrderId,
    string PickupName, string PickupCompany, string PickupAddress, string PickupPostcode, string PickupPhone, string PickupEmail,
    double PickupLat, double PickupLng,
    string DropName, string DropCompany, string DropAddress1, string? DropAddress2, string DropPostcode, string DropPhone, string DropEmail,
    double DropLat, double DropLng,
    DateTimeOffset DeliveryTimeLondon,  // scheduled "target ready" time in Europe/London
    string? DropInstructions,
    string? Reference // often orderId
);

public sealed record DeliveryResult(string Provider, string DeliveryId);