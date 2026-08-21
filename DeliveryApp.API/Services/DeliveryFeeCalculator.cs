using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

/// <summary>
/// حساب سعر التوصيل الفعلي بناءً على المسافة بين المحل والعميل.
/// القاعدة (القيم قابلة للتعديل من لوحة الأدمن عن طريق جدول DeliverySettings):
///   - أول FreeRadiusKm كيلومتر: يتم تحصيل سعر التوصيل الأساسي المحدد في المحل (Restaurant.DeliveryFee) بدون أي زيادة.
///   - كل كيلومتر إضافي (أو جزء منه) بعد أول FreeRadiusKm: يضاف ExtraFeePerKm جنيه.
///     مثال (بالقيم الافتراضية 3 كم / 10 جنيه): مسافة 3.5 كم => 0.5 كم زيادة => تُقرَّب لأعلى لكيلومتر كامل => +10 جنيه.
///     مثال: مسافة 5 كم => 2 كم زيادة => +20 جنيه.
/// </summary>
public static class DeliveryFeeCalculator
{
    // ── القيم الافتراضية (Fallback) لو حصل أي مشكلة في قراءة الإعدادات من الداتابيز ──
    public const double DefaultFreeRadiusKm = 3.0;
    public const decimal DefaultExtraFeePerKm = 10m;
    public const double DefaultMaxDeliveryZoneKm = 10.0;

    /// <summary>
    /// يجيب إعدادات التوصيل الحالية من الداتابيز (صف واحد بس، Id = 1).
    /// لو الجدول فاضي لأي سبب، بينشئ صف افتراضي تلقائيًا.
    /// </summary>
    public static async Task<(double FreeRadiusKm, decimal ExtraFeePerKm)> GetSettingsAsync(ApplicationDbContext db)
    {
        var settings = await db.DeliverySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
        {
            // Defensive fallback: لو لسبب ما الصف مش موجود (مثلاً startup SQL ما اتنفذش)
            var created = new DeliverySettings
            {
                FreeRadiusKm = DefaultFreeRadiusKm,
                ExtraFeePerKm = DefaultExtraFeePerKm,
                UpdatedAt = DateTime.UtcNow
            };
            db.DeliverySettings.Add(created);
            try { await db.SaveChangesAsync(); } catch { /* تجاهل لو حصل تعارض، هنستخدم القيم الافتراضية بالذاكرة */ }
            return (DefaultFreeRadiusKm, DefaultExtraFeePerKm);
        }

        return (settings.FreeRadiusKm, settings.ExtraFeePerKm);
    }

    /// <summary>
    /// يجيب أقصى مسافة توصيل (الزون) وسبب تقليل الزون لو موجود.
    /// منفصلة عن GetSettingsAsync عشان الأماكن اللي بتحسب سعر التوصيل بس ملهاش لازمة تحمّل الزون كل مرة.
    /// </summary>
    public static async Task<(double MaxDeliveryZoneKm, string? ZoneReducedReason)> GetZoneSettingsAsync(ApplicationDbContext db)
    {
        var settings = await db.DeliverySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
            return (DefaultMaxDeliveryZoneKm, null);

        return (settings.MaxDeliveryZoneKm, settings.ZoneReducedReason);
    }

    /// <summary>
    /// يحسب سعر التوصيل النهائي.
    /// </summary>
    /// <param name="baseFee">سعر التوصيل الأساسي المحدد في المحل (أول FreeRadiusKm كم)</param>
    /// <param name="distanceKm">المسافة الفعلية بين المحل والعميل بالكيلومتر</param>
    /// <param name="freeRadiusKm">المسافة اللي مش بياخد عليها زيادة (افتراضي 3 كم)</param>
    /// <param name="extraFeePerKm">الجنيه اللي بيتضاف عن كل كيلومتر زيادة أو جزء منه (افتراضي 10 جنيه)</param>
    public static decimal Calculate(decimal baseFee, double distanceKm, double freeRadiusKm = DefaultFreeRadiusKm, decimal extraFeePerKm = DefaultExtraFeePerKm)
    {
        if (distanceKm <= freeRadiusKm)
            return baseFee;

        var extraKm = distanceKm - freeRadiusKm;

        // أي جزء من الكيلومتر (حتى لو 100 متر) يتم تقريبه لأعلى لكيلومتر كامل
        var extraKmRounded = Math.Ceiling(extraKm - 0.000001); // تفادي مشاكل الفاصلة العشرية (floating point)
        if (extraKmRounded < 1) extraKmRounded = 1;

        var extraFee = (decimal)extraKmRounded * extraFeePerKm;
        return baseFee + extraFee;
    }

    /// <summary>
    /// حساب المسافة بين نقطتين بالكيلومتر (Haversine)
    /// </summary>
    public static double GetDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371; // نصف قطر الأرض بالكيلومتر
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * (Math.PI / 180);
}
