using System.Text.Json.Serialization;

public class SubmitOrderRequest
{
    public string UUID { get; set; } = "";

    // ApplePayToken is optional when you send a PaymentIntent id
    public string? ApplePayToken { get; set; }

    public string? Email { get; set; }
    public string? Name { get; set; }
    public int MiniAppId { get; set; }
    public decimal Total { get; set; }

    public List<BasketItem> Basket { get; set; } = new();
    public DeliveryInfo? Delivery { get; set; }

    // Optional snapshot (if you use it)
    public ShopSnapshot? ShopSnapshot { get; set; }

    // 👇 NEW: must be settable (not init-only) so model binding can populate it
    public string? StripePaymentIntentId { get; set; }
}

public class BasketItem
{
    public int Quantity { get; set; }
    public string? SquareId { get; set; }
    public decimal Price { get; set; }
    public int ProductId { get; set; }
    public string? Modifiers { get; set; }
    public string? Name { get; set; }
    public string? ProductName { get; set; }
}

public class DeliveryInfo
{
    public bool IsDelivery { get; set; }
    public string? ScheduledFor { get; set; }
    public AddressInfo? Address { get; set; }
}

public class AddressInfo
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Postcode { get; set; }
}

public class ShopSnapshot
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? Postcode { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
}