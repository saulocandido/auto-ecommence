namespace AutoCommerce.Shared.Events;

public static class EventTypes
{
    public const string ProductDiscovered = "product.discovered";
    public const string ProductApproved = "product.approved";
    public const string ProductPaused = "product.paused";
    public const string ProductKilled = "product.kill";
    public const string SupplierSelected = "supplier.selected";
    public const string SupplierPriceChanged = "supplier.price_changed";
    public const string SupplierStockChanged = "supplier.stock_changed";
    public const string OrderCreated = "order.created";
    public const string OrderSentToSupplier = "order.sent_to_supplier";
    public const string OrderFulfilled = "order.fulfilled";
    public const string OrderFulfillmentFailed = "order.fulfillment_failed";
    public const string PaymentSucceeded = "payment.succeeded";
    public const string PaymentFailed = "payment.failed";
    public const string PriceUpdated = "price.updated";
    public const string MarginAlert = "margin.alert";
    public const string AdsScale = "ads.scale";
    public const string ProductLinkVerified = "product.link_verified";
    public const string ProductLinkCorrected = "product.link_corrected";
    public const string ProductLinkInvalid = "product.link_invalid";
}
