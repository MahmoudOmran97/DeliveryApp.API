using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("DriverId", Name = "IX_DriverLocations_DriverId")]
[Index("OrderId", Name = "IX_DriverLocations_OrderId")]
[Index("Timestamp", Name = "IX_DriverLocations_Timestamp", AllDescending = true)]
public partial class DriverLocation
{
    [Key]
    public int Id { get; set; }

    public int DriverId { get; set; }

    public int? OrderId { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double? Speed { get; set; }

    public double? Heading { get; set; }

    public DateTime Timestamp { get; set; }

    [ForeignKey("DriverId")]
    [InverseProperty("DriverLocations")]
    public virtual Driver Driver { get; set; } = null!;

    [ForeignKey("OrderId")]
    [InverseProperty("DriverLocations")]
    public virtual Order? Order { get; set; }
}
