using System;

namespace DeliveryApp.API.Helpers
{
    /// <summary>
    /// ✅ Helper بيحوّل أي وقت UTC لتوقيت مصر المحلي.
    /// 
    /// استخدمه بس لما محتاج تعرض/تبعت نص جاهز فيه وقت مصري من نفس السيرفر
    /// (زي نص إيميل، إشعار Push بصيغة نصية، أو PDF). أما بيانات الـ API العادية
    /// (JSON) سيبها UTC زي ما هي، عشان كل تطبيق (المتصفح أو الموبايل) بيحوّلها
    /// تلقائي لتوقيت الجهاز بتاعه.
    /// 
    /// الكود بيدور على اسم المنطقة الزمنية سواء السيرفر شغال Windows
    /// ("Egypt Standard Time") أو Linux/Docker ("Africa/Cairo")، وده مهم لأن
    /// الاسمين مختلفين حسب نظام التشغيل.
    /// </summary>
    public static class EgyptTimeZoneHelper
    {
        private static readonly TimeZoneInfo EgyptTimeZone = ResolveEgyptTimeZone();

        /// <summary>الوقت الحالي بتوقيت مصر (مش UTC).</summary>
        public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EgyptTimeZone);

        /// <summary>يحوّل أي DateTime (لازم يكون UTC أو معلّم Utc) لتوقيت مصر.</summary>
        public static DateTime ToEgyptTime(DateTime utcDateTime)
        {
            var utc = utcDateTime.Kind == DateTimeKind.Utc
                ? utcDateTime
                : DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);

            return TimeZoneInfo.ConvertTimeFromUtc(utc, EgyptTimeZone);
        }

        private static TimeZoneInfo ResolveEgyptTimeZone()
        {
            try
            {
                // Windows
                return TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                try
                {
                    // Linux / Docker (IANA)
                    return TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
                }
                catch (TimeZoneNotFoundException)
                {
                    // fallback: مصر بتوقيت ثابت UTC+2 (لو الاتنين مش موجودين على السيرفر)
                    return TimeZoneInfo.CreateCustomTimeZone("Egypt_Fallback", TimeSpan.FromHours(2), "Egypt (fallback)", "Egypt (fallback)");
                }
            }
        }
    }
}