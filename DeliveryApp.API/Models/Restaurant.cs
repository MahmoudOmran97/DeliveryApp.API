using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

public partial class Restaurant
{
    [Key]
    public int Id { get; set; }

    [StringLength(150)]
    public string Name { get; set; } = null!;

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(300)]
    public string Address { get; set; } = null!;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    [StringLength(500)]
    public string? ImageUrl { get; set; }

    [StringLength(500)]
    public string? CoverImageUrl { get; set; }

    [StringLength(20)]
    public string? Phone { get; set; }

    public double Rating { get; set; }

    public int TotalRatings { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal DeliveryFee { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal MinOrderAmount { get; set; }

    public int EstimatedTime { get; set; }

    /// <summary>
    /// نوع المحل: Restaurant | Pharmacy | Grocery | Supermarket | Vegetables | Drinks | Accessories
    /// </summary>
    [StringLength(50)]
    public string StoreType { get; set; } = "Restaurants";

    public bool IsOpen { get; set; }

    public bool IsActive { get; set; }

    public int? OwnerUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey("OwnerUserId")]
    [InverseProperty("OwnedRestaurants")]
    public virtual User? Owner { get; set; }

    [InverseProperty("Restaurant")]
    public virtual ICollection<Category> Categories { get; set; } = new List<Category>();

    [InverseProperty("Restaurant")]
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    [InverseProperty("Restaurant")]
    public virtual ICollection<Rating> Ratings { get; set; } = new List<Rating>();
}