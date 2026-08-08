using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

/// <summary>
/// نوع الجهة اللي بيتحسب عليها الاشتراك: محل (Restaurant) أو سواق (Driver).
/// </summary>
public enum RevenueEntityType
{
    Store = 0,
    Driver = 1
}

/// <summary>
/// نوع احتساب الاشتراك: نسبة % من قيمة الطلبات (SubTotal)، أو مبلغ ثابت لكل دورة (شهر).
/// </summary>
public enum SubscriptionType
{
    Percentage = 0,
    Fixed = 1
}

/// <summary>
/// إعدادات اشتراك/عمولة منصة الدليفري من محل أو من سواق.
/// صف واحد نشط لكل (EntityType, RestaurantId/DriverId).
/// </summary>
public partial class SubscriptionPlan
{
    [Key]
    public int Id { get; set; }

    public RevenueEntityType EntityType { get; set; }

    public int? RestaurantId { get; set; }

    public int? DriverId { get; set; }

    public SubscriptionType Type { get; set; }

    /// <summary>
    /// لو Percentage: القيمة نسبة مئوية (مثلاً 5 يعني 5%).
    /// لو Fixed: القيمة مبلغ بالجنيه مستحق كل دورة (شهر) بغض النظر عن عدد الطلبات.
    /// </summary>
    [Column(TypeName = "decimal(10, 2)")]
    public decimal Value { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("RestaurantId")]
    public virtual Restaurant? Restaurant { get; set; }

    [ForeignKey("DriverId")]
    public virtual Driver? Driver { get; set; }
}
