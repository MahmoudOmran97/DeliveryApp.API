using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("OrderId", Name = "UQ__Ratings__C3905BCECE0D0C32", IsUnique = true)]
public partial class Rating
{
    [Key]
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int CustomerId { get; set; }

    public int? DriverId { get; set; }

    public int RestaurantId { get; set; }

    public int? DriverRating { get; set; }

    public int RestaurantRating { get; set; }

    public int? FoodRating { get; set; }

    [StringLength(500)]
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey("CustomerId")]
    [InverseProperty("Ratings")]
    public virtual User Customer { get; set; } = null!;

    [ForeignKey("DriverId")]
    [InverseProperty("Ratings")]
    public virtual Driver? Driver { get; set; }

    [ForeignKey("OrderId")]
    [InverseProperty("Rating")]
    public virtual Order Order { get; set; } = null!;

    [ForeignKey("RestaurantId")]
    [InverseProperty("Ratings")]
    public virtual Restaurant Restaurant { get; set; } = null!;
}
