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

    public virtual DbSet<ProductVariant> ProductVariants { get; set; }

    public virtual DbSet<PointTransaction> PointTransactions { get; set; }

    public virtual DbSet<Rating> Ratings { get; set; }

    public virtual DbSet<Restaurant> Restaurants { get; set; }

    public virtual DbSet<User> Users { get; set; }


    public virtual DbSet<ChatMessage> ChatMessages { get; set; }
    public virtual DbSet<Banner> Banners { get; set; }
    public virtual DbSet<Coupon> Coupons { get; set; }
    public virtual DbSet<Deal> Deals { get; set; }
    public virtual DbSet<UserCoupon> UserCoupons { get; set; }

    public virtual DbSet<PrescriptionRequest> PrescriptionRequests { get; set; }

    public virtual DbSet<PrescriptionMessage> PrescriptionMessages { get; set; }

    // ✅ الجديد: جدول أكواد الـ OTP (تسجيل / نسيت كلمة المرور)
    public virtual DbSet<OtpCode> OtpCodes { get; set; }

    // ✅ الجديد: إعدادات سعر التوصيل (قابلة للتعديل من الأدمن)
    public virtual DbSet<DeliverySettings> DeliverySettings { get; set; }

    public virtual DbSet<AiSettings> AiSettings { get; set; }
    public virtual DbSet<SupportSession> SupportSessions { get; set; }
    public virtual DbSet<SupportMessage> SupportMessages { get; set; }
    public virtual DbSet<Complaint> Complaints { get; set; }

    public virtual DbSet<SubscriptionPlan> SubscriptionPlans { get; set; }

    public virtual DbSet<RevenueSettlement> RevenueSettlements { get; set; }

    public virtual DbSet<SiteLink> SiteLinks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}