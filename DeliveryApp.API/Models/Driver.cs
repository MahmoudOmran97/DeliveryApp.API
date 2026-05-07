using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("UserId", Name = "UQ__Drivers__1788CC4DCA0C3312", IsUnique = true)]
public partial class Driver
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [StringLength(50)]
    public string VehicleType { get; set; } = null!;

    [StringLength(20)]
    public string LicensePlate { get; set; } = null!;

    [StringLength(20)]
    public string? NationalId { get; set; }

    public double Rating { get; set; }

    public int TotalRatings { get; set; }

    public int TotalDeliveries { get; set; }

    public bool IsOnline { get; set; }

    public bool IsAvailable { get; set; }

    public double? CurrentLatitude { get; set; }

    public double? CurrentLongitude { get; set; }

    public DateTime? LastLocationUpdate { get; set; }

    public bool IsVerified { get; set; }

    public DateTime JoinedAt { get; set; }

    [InverseProperty("Driver")]
    public virtual ICollection<DriverLocation> DriverLocations { get; set; } = new List<DriverLocation>();

    [InverseProperty("Driver")]
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    [InverseProperty("Driver")]
    public virtual ICollection<Rating> Ratings { get; set; } = new List<Rating>();

    [ForeignKey("UserId")]
    [InverseProperty("Driver")]
    public virtual User User { get; set; } = null!;
}
