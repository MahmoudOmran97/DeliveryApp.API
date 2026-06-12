using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

public class Banner
{
    [Key]
    public int Id { get; set; }

    [StringLength(200)]
    public string Title { get; set; } = null!;

    [StringLength(500)]
    public string? SubTitle { get; set; }

    [StringLength(500)]
    public string? ImageUrl { get; set; }

    [StringLength(300)]
    public string? ActionUrl { get; set; }  // deep link e.g. restaurant/5

    [StringLength(50)]
    public string? BackgroundColor { get; set; } // hex e.g. #FF5722

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? StartsAt { get; set; }

    public DateTime? EndsAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
