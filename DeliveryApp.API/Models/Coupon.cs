using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class Coupon
{
    [Key]
    public int Id { get; set; }

    [StringLength(50)]
    public string Code { get; set; } = null!;  // e.g. SAVE20

    [StringLength(200)]
    public string Title { get; set; } = null!;

    [StringLength(500)]
    public string? Description { get; set; }

    // DiscountType: "Percentage" or "Fixed"
    [StringLength(20)]
    public string DiscountType { get; set; } = "Fixed";

    [Column(TypeName = "decimal(10,2)")]
    public decimal DiscountValue { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? MinOrderAmount { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? MaxDiscount { get; set; }

    public int? RestaurantId { get; set; }  // null = applies to all

    // ✅ لو الكوبون ده ناتج عن استبدال نقاط، بيتحدد لصاحبه بس (null = كوبون عام للكل)
    public int? OwnerUserId { get; set; }

    public int? UsageLimit { get; set; }  // null = unlimited

    public int UsedCount { get; set; } = 0;

    public bool IsActive { get; set; } = true;

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("RestaurantId")]
    public virtual Restaurant? Restaurant { get; set; }
}
