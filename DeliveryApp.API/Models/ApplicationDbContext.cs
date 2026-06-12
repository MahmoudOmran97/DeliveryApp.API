using Microsoft.EntityFrameworkCore;
using System.Reflection;
using static Azure.Core.HttpHeader;

namespace DeliveryApp.API.Models;

public partial class ApplicationDbContext : DbContext
{
    public ApplicationDbContext()
    {
    }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Category> Categories { get; set; }

    public virtual DbSet<Driver> Drivers { get; set; }

    public virtual DbSet<DriverLocation> DriverLocations { get; set; }

    public virtual DbSet<Notification> Notifications { get; set; }

    public virtual DbSet<Order> Orders { get; set; }

    public virtual DbSet<OrderItem> OrderItems { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<Rating> Ratings { get; set; }

    public virtual DbSet<Restaurant> Restaurants { get; set; }

    public virtual DbSet<User> Users { get; set; }


    public virtual DbSet<ChatMessage> ChatMessages { get; set; }
    public virtual DbSet<Banner> Banners { get; set; }
    public virtual DbSet<Coupon> Coupons { get; set; }
    public virtual DbSet<Deal> Deals { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}