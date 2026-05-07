using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

[Index("IsRead", Name = "IX_Notifications_IsRead")]
[Index("UserId", Name = "IX_Notifications_UserId")]
public partial class Notification
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [StringLength(200)]
    public string Title { get; set; } = null!;

    [StringLength(500)]
    public string Body { get; set; } = null!;

    [StringLength(50)]
    public string Type { get; set; } = null!;

    public bool IsRead { get; set; }

    public int? OrderId { get; set; }

    public DateTime CreatedAt { get; set; }

    [ForeignKey("UserId")]
    [InverseProperty("Notifications")]
    public virtual User User { get; set; } = null!;
}
