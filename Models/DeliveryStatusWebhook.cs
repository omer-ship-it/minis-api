namespace Minis.Models
{
    using System.Text.Json.Serialization;

    public sealed class DeliveryStatusWebhook
    {
        [JsonPropertyName("orderId")]
        public string? OrderId { get; set; }

        [JsonPropertyName("deliveryStatus")]
        public string? DeliveryStatus { get; set; }

        [JsonPropertyName("driver")]
        public DriverInfo? Driver { get; set; }

        [JsonPropertyName("eta")]
        public EtaInfo? Eta { get; set; }
    }

    public sealed class DriverInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("phone")]
        public string? Phone { get; set; }

        [JsonPropertyName("location")]
        public DriverLocation? Location { get; set; }
    }

    public sealed class DriverLocation
    {
        [JsonPropertyName("lat")]
        public string? Lat { get; set; }

        // JSON key is "long" — map it explicitly
        [JsonPropertyName("long")]
        public string? Long { get; set; }
    }

    public sealed class EtaInfo
    {
        [JsonPropertyName("pickup")]
        public string? Pickup { get; set; }

        [JsonPropertyName("dropoff")]
        public string? Dropoff { get; set; }
    }

    public static class StatusMapper
{
    public static int ToCode(string? statusText) => statusText?.ToLowerInvariant() switch
    {
        "pending" or "looking_for_driver"         => 1,
        "driver_en_route_to_pickup"               => 2,
        "driver_at_pickup"                        => 3,
        "in_transit" or "driver_at_dropoff"       => 4,
        "success"                                  => 5,
        _                                          => -1
    };
}


}
