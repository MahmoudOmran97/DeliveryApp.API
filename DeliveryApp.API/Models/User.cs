using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("Email", Name = "IX_Users_Email")]
[Index("Role", Name = "IX_Users_Role")]
[Index("Email", Name = "UQ__Users__A9D105340912770B", IsUnique = true)]
public partial class User
{
    [Key]
    public int Id { get; set; }

    [StringLength(100)]
    public string FullName { get; set; } = null!;

    [StringLength(150)]
    public string Email { get; set; } = null!;

    [StringLength(500)]
    public string PasswordHash { get; set; } = null!;

    [StringLength(20)]
    public string Phone { get; set; } = null!;

    [StringLength(20)]
    public string Role { get; set; } = null!;

    [StringLength(300)]
    public string? Address { get; set; }

    [StringLength(500)]
    public string? ProfileImageUrl { get; set; }

    [Column("FCMToken")]
    [StringLength(500)]
    public string? Fcmtoken { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [InverseProperty("User")]
    public virtual Driver? Driver { get; set; }

    [InverseProperty("User")]
    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    [InverseProperty("Customer")]
    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    [InverseProperty("Customer")]
    public virtual ICollection<Rating> Ratings { get; set; } = new List<Rating>();
}
