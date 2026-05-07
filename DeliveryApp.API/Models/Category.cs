using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Models;

public partial class Category
{
    [Key]
    public int Id { get; set; }

    public int RestaurantId { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = null!;

    [StringLength(500)]
    public string? ImageUrl { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }

    [InverseProperty("Category")]
    public virtual ICollection<Product> Products { get; set; } = new List<Product>();

    [ForeignKey("RestaurantId")]
    [InverseProperty("Categories")]
    public virtual Restaurant Restaurant { get; set; } = null!;
}
