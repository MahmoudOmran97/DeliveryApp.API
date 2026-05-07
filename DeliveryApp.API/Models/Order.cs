using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("CreatedAt", Name = "IX_Orders_CreatedAt", AllDescending = true)]
[Index("CustomerId", Name = "IX_Orders_CustomerId")]
[Index("DriverId", Name = "IX_Orders_DriverId")]
[Index("RestaurantId", Name = "IX_Orders_RestaurantId")]
[Index("Status", Name = "IX_Orders_Status")]
public partial class Order
{
    [Key]
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public int RestaurantId { get; set; }

    public int? DriverId { get; set; }

    [StringLength(30)]
    public string Status { get; set; } = null!;

    [Column(TypeName = "decimal(10, 2)")]
    public decimal SubTotal { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal DeliveryFee { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Discount { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal TotalAmount { get; set; }

    [StringLength(300)]
    public string DeliveryAddress { get; set; } = null!;

    public double DeliveryLatitude { get; set; }

    public double DeliveryLongitude { get; set; }

    [StringLength(300)]
    public string? DeliveryNotes { get; set; }

    [StringLength(30)]
    public string PaymentMethod { get; set; } = null!;

    [StringLength(20)]
    public string PaymentStatus { get; set; } = null!;

    public int? EstimatedDelivery { get; set; }

    [StringLength(300)]
    public string? CancellationReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime? PickedUpAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    [ForeignKey("CustomerId")]
    [InverseProperty("Orders")]
    public virtual User Customer { get; set; } = null!;

    [ForeignKey("DriverId")]
    [InverseProperty("Orders")]
    public virtual Driver? Driver { get; set; }

    [InverseProperty("Order")]
    public virtual ICollection<DriverLocation> DriverLocations { get; set; } = new List<DriverLocation>();

    [InverseProperty("Order")]
    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    [InverseProperty("Order")]
    public virtual Payment? Payment { get; set; }

    [InverseProperty("Order")]
    public virtual Rating? Rating { get; set; }

    [ForeignKey("RestaurantId")]
    [InverseProperty("Orders")]
    public virtual Restaurant Restaurant { get; set; } = null!;
}
