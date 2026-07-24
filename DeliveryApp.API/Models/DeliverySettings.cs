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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
