using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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
        // =====================================================================
        // ✅ إصلاح مشكلة التوقيت (Timezone Fix)
        // =====================================================================
        // المشكلة: التواريخ بتتسجل بالفعل بتوقيت UTC (DateTime.UtcNow) في أغلب
        // الكود، وده صح، لكن SQL Server بيرجّع الـ DateTime من غير علامة توضح
        // إنه UTC (DateTimeKind.Unspecified). فلما الـ API يرجّع البيانات JSON
        // للتطبيقات (Customer/Driver/Admin)، القيمة بتتبعت من غير حرف "Z" في
        // الآخر، فالتطبيقات بتفهمها غلط وتعتبرها وقت محلي زي ما هي (يعني بتوقيت
        // السيرفر الخام) بدل ما تحوّلها لتوقيت مصر تلقائي.
        //
        // الحل: أي عمود DateTime بييجي من قاعدة البيانات، نعلّمه إنه UTC
        // (DateTimeKind.Utc) قبل ما يوصل للـ JSON. وبكده أي تطبيق (المتصفح،
        // MAUI، JS) هيقدر يحوّله لتوقيت مصر المحلي عنده تلقائي وبشكل صحيح.
        // القيم المخزنة في الداتابيز نفسها مش بتتغيّر خالص، بس الـ "توصيف" بتاعها
        // بيبقى صح.
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            toDb => toDb, // بيتسجل زي ما هو (UTC) في القاعدة
            fromDb => DateTime.SpecifyKind(fromDb, DateTimeKind.Utc)); // بيتعلّم إنه UTC وهو راجع

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            toDb => toDb,
            fromDb => fromDb.HasValue ? DateTime.SpecifyKind(fromDb.Value, DateTimeKind.Utc) : fromDb);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}