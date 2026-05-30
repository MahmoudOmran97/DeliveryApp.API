// ملف جديد: DeliveryApp.API/Authorization/RestaurantOwnerAuthorization.cs
// ─────────────────────────────────────────────────────────────────────────────
// Helper مركزي للتحقق من صلاحيات صاحب المطعم
// بيتاكد إن اليوزر:
//   1. مسجل دخول
//   2. دوره Restaurant (أو Admin)
//   3. هو فعلاً الـ Owner بتاع المطعم المطلوب
// ─────────────────────────────────────────────────────────────────────────────

using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Authorization;

public static class RestaurantOwnerAuth
{
    /// <summary>
    /// بيجيب UserId من الـ JWT token
    /// </summary>
    public static int? GetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? user.FindFirstValue("sub");
        return int.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// بيتحقق إن اليوزر هو Owner بتاع المطعم ده (أو Admin)
    /// بيرجع null لو الصلاحية تمام، أو IActionResult بالـ error
    /// </summary>
    public static async Task<IActionResult?> CheckOwnerAsync(
        ClaimsPrincipal user,
        int restaurantId,
        ApplicationDbContext db)
    {
        var userId = GetUserId(user);
        if (userId is null)
            return new UnauthorizedObjectResult(new { message = "غير مصرح" });

        var role = user.FindFirstValue(ClaimTypes.Role);

        // Admin يقدر يعمل أي حاجة
        if (role == "Admin")
            return null;

        if (role != "Restaurant")
            return new ObjectResult(new { message = "هذه العملية خاصة بأصحاب المطاعم فقط" })
            { StatusCode = 403 };

        // تأكد إن المطعم ده فعلاً بتاعه
        var isOwner = await db.Restaurants
            .AnyAsync(r => r.Id == restaurantId && r.OwnerUserId == userId);

        if (!isOwner)
            return new ObjectResult(new { message = "ليس لديك صلاحية على هذا المطعم" })
            { StatusCode = 403 };

        return null; // كل حاجة تمام
    }

    /// <summary>
    /// بيجيب RestaurantId بتاع اليوزر من الـ DB
    /// (لما صاحب المطعم يعمل login ومش محتاج يكتب restaurantId)
    /// </summary>
    public static async Task<int?> GetOwnerRestaurantIdAsync(
        ClaimsPrincipal user,
        ApplicationDbContext db)
    {
        var userId = GetUserId(user);
        if (userId is null) return null;

        var restaurant = await db.Restaurants
            .Where(r => r.OwnerUserId == userId && r.IsActive)
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        return restaurant == 0 ? null : restaurant;
    }
}
