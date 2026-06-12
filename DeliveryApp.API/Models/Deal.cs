using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

// تخفيضات المطاعم على منتجات معينة
public class Deal
{
    [Key]
    public int Id { get; set; }

    [StringLength(200)]
    public string Title { get; set; } = null!;

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(500)]
    public string? ImageUrl { get; set; }

    public int? RestaurantId { get; set; }

    public int? ProductId { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? OriginalPrice { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal? DiscountedPrice { get; set; }

    public int? DiscountPercent { get; set; }  // e.g. 30 = 30% off

    [StringLength(50)]
    public string? BadgeText { get; set; }  // e.g. "خصم 30%", "عرض محدود"

    [StringLength(50)]
    public string? BadgeColor { get; set; }  // hex

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("RestaurantId")]
    public virtual Restaurant? Restaurant { get; set; }

    [ForeignKey("ProductId")]
    public virtual Product? Product { get; set; }
}
