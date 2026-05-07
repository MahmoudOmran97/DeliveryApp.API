using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("OrderId", Name = "UQ__Payments__C3905BCE10717A08", IsUnique = true)]
public partial class Payment
{
    [Key]
    public int Id { get; set; }

    public int OrderId { get; set; }

    [StringLength(50)]
    public string Provider { get; set; } = null!;

    [StringLength(200)]
    public string? TransactionId { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Amount { get; set; }

    [StringLength(10)]
    public string Currency { get; set; } = null!;

    [StringLength(20)]
    public string Status { get; set; } = null!;

    [StringLength(200)]
    public string? PaymobOrderId { get; set; }

    [StringLength(300)]
    public string? RefundReason { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey("OrderId")]
    [InverseProperty("Payment")]
    public virtual Order Order { get; set; } = null!;
}
