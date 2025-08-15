namespace Minis.Models
{
    public class DeliveryStatusUpdate
    {
        public string? Provider { get; set; }      // "gophr", "orkestro", etc.
        public string? Status { get; set; }        // "driver_assigned", "on_the_way", etc.
        public int? EtaMinutes { get; set; }

        // Driver fields (optional)
        public string? DriverName { get; set; }
        public string? DriverPhone { get; set; }
        public string? Vehicle { get; set; }
        public string? Plate { get; set; }

        // Location fields (optional)
        public double? Lat { get; set; }
        public double? Lng { get; set; }
        public double? Accuracy { get; set; }
        public double? Bearing { get; set; }

        // If you want to store the original webhook payload as-is
        public object? Raw { get; set; }
    }
}
