using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

public interface IPointsService
{
    int CalculateEarnedPoints(decimal orderTotal);
    Task AwardOrderPointsAsync(int userId, int orderId, decimal orderTotal, ApplicationDbContext db);
    Task<(bool Ok, string? Message, Coupon? Coupon)> RedeemPointsAsync(int userId, int points, ApplicationDbContext db);
}

public class PointsService : IPointsService
{
    public int CalculateEarnedPoints(decimal orderTotal)
    {
        if (orderTotal < 200) return 0;
        var points = 20;
        var extra = orderTotal - 200;
        points += (int)Math.Floor(extra / 100m) * 10;
        return points;
    }

    public async Task AwardOrderPointsAsync(int userId, int orderId, decimal orderTotal, ApplicationDbContext db)
    {
        var earned = CalculateEarnedPoints(orderTotal);
        if (earned <= 0) return;

        var already = await db.PointTransactions.AnyAsync(t => t.UserId == userId && t.OrderId == orderId && t.Amount > 0);
        if (already) return;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return;

        user.PointsBalance += earned;
        db.PointTransactions.Add(new PointTransaction
        {
            UserId = userId,
            Amount = earned,
            Title = $"طلب #{orderId}",
            Description = $"نقاط من فاتورة {orderTotal:F0} جنيه",
            OrderId = orderId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task<(bool Ok, string? Message, Coupon? Coupon)> RedeemPointsAsync(int userId, int points, ApplicationDbContext db)
    {
        if (points < 100)
            return (false, "الحد الأدنى للاستبدال 100 نقطة", null);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return (false, "User not found", null);
        if (user.PointsBalance < points)
            return (false, "رصيد النقاط غير كافٍ", null);

        var discount = points / 10m;
        var code = $"PTS{userId}{DateTime.UtcNow:yyMMddHHmmss}";

        var coupon = new Coupon
        {
            Code = code,
            Title = $"كوبون نقاط ({points} نقطة)",
            Description = "تم إنشاؤه من نقاط المكافآت",
            DiscountType = "Fixed",
            DiscountValue = discount,
            MinOrderAmount = discount,
            IsActive = true,
            OwnerUserId = userId, // ✅ الكوبون ده خاص بالمستخدم اللي استبدل نقاطه، مش عام لكل الكستمر
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow
        };

        user.PointsBalance -= points;
        db.Coupons.Add(coupon);
        db.PointTransactions.Add(new PointTransaction
        {
            UserId = userId,
            Amount = -points,
            Title = "استبدال نقاط",
            Description = $"كوبون خصم {discount:F0} جنيه — {code}",
            CouponId = coupon.Id,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
        return (true, null, coupon);
    }
}
