//using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;

namespace DeliveryApp.API.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

     /*   public DbSet<User> Users { get; set; }
        public DbSet<Restaurant> Restaurants { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public DbSet<Driver> Drivers { get; set; }
        public DbSet<Payment> Payments { get; set; }
        public DbSet<Notification> Notifications { get; set; }
        public DbSet<Rating> Ratings { get; set; }
        public DbSet<DriverLocation> DriverLocations { get; set; }*/
    }
}