using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class UserCoupon
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    public int CouponId { get; set; }

    public DateTime UsedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public virtual User User { get; set; } = null!;

    [ForeignKey("CouponId")]
    public virtual Coupon Coupon { get; set; } = null!;
}
