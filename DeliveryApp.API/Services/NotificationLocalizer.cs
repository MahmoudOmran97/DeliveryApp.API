namespace DeliveryApp.API.Services;

/// <summary>
/// Push / in-app notification texts in Arabic and English.
/// Wording is store-type aware (pharmacy, supermarket, etc.) — not restaurant-only.
/// </summary>
public static class NotificationLocalizer
{
    public static string NormalizeLang(string? lang) =>
        lang?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) == true ? "ar" : "en";

    public static (string Title, string Body) OrderPlaced(string? lang, string storeName, string? storeType)
    {
        lang = NormalizeLang(lang);
        return lang == "ar"
            ? ("تم الطلب! 🎉", $"تم تقديم طلبك من {storeName}.")
            : ("Order Placed! 🎉", $"Your order from {storeName} has been placed.");
    }

    public static (string Title, string Body) NewOrderForOwner(string? lang, int orderId)
    {
        lang = NormalizeLang(lang);
        return lang == "ar"
            ? ("🛍️ طلب جديد!", $"وصلك طلب جديد برقم #{orderId}")
            : ("🛍️ New Order!", $"You received a new order #{orderId}");
    }

    public static (string Title, string Body) StatusUpdate(string? lang, string status, string? storeType = null)
    {
        lang = NormalizeLang(lang);
        return status switch
        {
            "Accepted" => lang == "ar"
                ? ("تم قبول الطلب!", "تم قبول طلبك.")
                : ("Order Accepted!", "Your order has been accepted."),

            "Preparing" => lang == "ar"
                ? ("جاري التجهيز", $"{GetStoreLabel(lang, storeType)} بيجهّز طلبك.")
                : ("Preparing Order", $"{GetStoreLabel(lang, storeType)} is preparing your order."),

            "ReadyForPickup" => lang == "ar"
                ? ("الطلب جاهز!", "طلبك جاهز للاستلام من السائق.")
                : ("Order Ready!", "Your order is ready for pickup by the driver."),

            "OnTheWay" => lang == "ar"
                ? ("السائق في الطريق!", "طلبك في الطريق إليك.")
                : ("Driver On The Way!", "Your order is on its way."),

            "Delivered" => lang == "ar"
                ? ("تم التوصيل!", "تم توصيل طلبك بنجاح. بالهناء والشفاء!")
                : ("Order Delivered!", "Your order has been delivered. Enjoy!"),

            "Rejected" => lang == "ar"
                ? ("تم رفض الطلب", "نأسف، تم رفض طلبك.")
                : ("Order Rejected", "Sorry, your order was rejected."),

            "Cancelled" => lang == "ar"
                ? ("تم إلغاء الطلب", "تم إلغاء طلبك.")
                : ("Order Cancelled", "Your order has been cancelled."),

            _ => lang == "ar"
                ? ("تحديث الطلب", "تم تحديث حالة طلبك.")
                : ("Order Update", "Your order status has been updated.")
        };
    }

    private static string GetStoreLabel(string lang, string? storeType)
    {
        var type = storeType?.Trim() ?? "Restaurants";
        if (lang == "ar")
        {
            return type switch
            {
                "Pharmacy" => "الصيدلية",
                "Grocery" => "البقالة",
                "Supermarket" => "السوبر ماركت",
                "Vegetables" => "محل الخضار",
                "Drinks" => "محل المشروبات",
                "Accessories" => "محل الإكسسوارات",
                _ => "المتجر"
            };
        }

        return type switch
        {
            "Pharmacy" => "The pharmacy",
            "Grocery" => "The grocery store",
            "Supermarket" => "The supermarket",
            "Vegetables" => "The produce store",
            "Drinks" => "The drinks store",
            "Accessories" => "The accessories store",
            _ => "The store"
        };
    }
}
