using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class PointTransaction
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    public int Amount { get; set; }

    [StringLength(200)]
    public string Title { get; set; } = null!;

    [StringLength(300)]
    public string? Description { get; set; }

    public int? OrderId { get; set; }

    public int? CouponId { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(UserId))]
    public virtual User User { get; set; } = null!;
}
