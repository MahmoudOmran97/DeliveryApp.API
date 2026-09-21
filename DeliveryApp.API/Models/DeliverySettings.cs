using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

/// <summary>
/// إعدادات حساب سعر التوصيل، قابلة للتعديل من لوحة الأدمن.
/// فيه صف واحد بس دايمًا (Id = 1).
/// </summary>
public class DeliverySettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>المسافة (بالكيلومتر) اللي بيتحصل عندها سعر التوصيل الأساسي للمحل فقط بدون أي زيادة</summary>
    public double FreeRadiusKm { get; set; } = 3.0;

    /// <summary>الجنيه اللي بيتضاف عن كل كيلومتر زيادة (أو جزء منه) بعد FreeRadiusKm</summary>
    [Column(TypeName = "decimal(10, 2)")]
    public decimal ExtraFeePerKm { get; set; } = 10m;

    /// <summary>
    /// أقصى مسافة (بالكيلومتر) يقدر العميل يطلب فيها من الأساس (الزون).
    /// أي محل أبعد من كده مش هيظهر للعميل، وأي عنوان توصيل أبعد من كده هيترفض في الـ Checkout.
    /// ده منفصل عن FreeRadiusKm (اللي بيتحكم في سعر التوصيل بس).
    /// </summary>
    public double MaxDeliveryZoneKm { get; set; } = 10.0;

    /// <summary>
    /// سبب اختياري بيظهر للعميل لو الأدمن قلل الزون (MaxDeliveryZoneKm) عن قيمته المعتادة،
    /// عشان العميل يفهم ليه المسافة قصيرة النهاردة من غير ما يحس إن الخدمة "مقفولة".
    /// مثال: "بنقلل نطاق التوصيل بالليل لحد ما نوفر سائقين كفاية للمسافات البعيدة".
    /// لو فاضي، الأبلكيشن بيعرض رسالة افتراضية عادية.
    /// </summary>
    [MaxLength(300)]
    public string? ZoneReducedReason { get; set; }

    /// <summary>
    /// نطاق ظهور الطلبات المتاحة للسائق (بالكيلومتر): السائق بيشوف بس الطلبات اللي المحل بتاعها
    /// على بُعد أقل من أو يساوي المسافة دي من موقعه الحالي. الأدمن هو اللي بيتحكم فيها.
    /// القيمة الافتراضية 1 كم (نفس السلوك القديم لما كانت ثابتة في الكود).
    /// </summary>
    public double DriverOrdersRadiusKm { get; set; } = 1.0;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
