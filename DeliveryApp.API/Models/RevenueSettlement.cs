using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

/// <summary>
/// حالة تحصيل استحقاق المنصة من المحل/السواق للدورة دي.
/// </summary>
public enum SettlementStatus
{
    Unpaid = 0,
    PartiallyPaid = 1,
    Paid = 2
}

/// <summary>
/// سجل استحقاق دوري (شهري) لأرباح المنصة من محل أو من سواق.
/// بيتولد من الطلبات المكتملة (Delivered) في الفترة، وبيتحصّل يدويًا من الأدمن.
/// </summary>
public partial class RevenueSettlement
{
    [Key]
    public int Id { get; set; }

    public RevenueEntityType EntityType { get; set; }

    public int? RestaurantId { get; set; }

    public int? DriverId { get; set; }

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    /// <summary>
    /// الأساس اللي اتحسبت عليه النسبة في الفترة دي:
    /// للمحل (Store) = مجموع SubTotal (قيمة الأوردرات من غير تمن التوصيل).
    /// للسواق (Driver) = مجموع DeliveryFee (تمن التوصيل بس، مش إجمالي الأوردر).
    /// </summary>
    [Column(TypeName = "decimal(10, 2)")]
    public decimal OrdersTotal { get; set; }

    public int OrdersCount { get; set; }

    /// <summary>النوع والقيمة المستخدمين وقت الحساب (نسخة من الـ Plan وقتها، عشان لو اتغير الاشتراك بعدين السجل القديم يفضل زي ما هو)</summary>
    public SubscriptionType PlanType { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal PlanValue { get; set; }

    /// <summary>المبلغ المستحق على المحل/السواق للمنصة عن الفترة دي</summary>
    [Column(TypeName = "decimal(10, 2)")]
    public decimal AmountDue { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal AmountPaid { get; set; }

    public SettlementStatus Status { get; set; } = SettlementStatus.Unpaid;

    public int? CollectedByAdminId { get; set; }

    public DateTime? PaidAt { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("RestaurantId")]
    public virtual Restaurant? Restaurant { get; set; }

    [ForeignKey("DriverId")]
    public virtual Driver? Driver { get; set; }
}
