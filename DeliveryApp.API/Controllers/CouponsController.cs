
using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CouponsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public CouponsController(ApplicationDbContext db) => _db = db;

    // GET api/coupons  — returns all active coupons (for rewards/coupons screen)
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
    {
        var now = DateTime.UtcNow;
        var coupons = await _db.Coupons
            .Where(c => c.IsActive && (c.ExpiresAt == null || c.ExpiresAt >= now))
            .OrderByDescending(c => c.DiscountValue)
            .Select(c => new
            {
                c.Id,
                c.Code,
                c.Title,
                c.Description,
                c.DiscountType,
                c.DiscountValue,
                c.MinOrderAmount,
                c.MaxDiscount,
                c.RestaurantId,
                RestaurantName = c.Restaurant != null ? c.Restaurant.Name : null,
                c.UsageLimit,
                c.UsedCount,
                c.ExpiresAt,
                ExpiresInDays = c.ExpiresAt.HasValue
                    ? (int?)(c.ExpiresAt.Value - now).TotalDays
                    : null
            })
            .ToListAsync();

        return Ok(coupons);
    }

    // POST api/coupons/validate — check coupon before checkout
    [HttpPost("validate")]
    [Authorize]
    public async Task<IActionResult> Validate([FromBody] ValidateCouponRequest req)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        var userId = int.Parse(userIdClaim?.Value!);
        
        var now = DateTime.UtcNow;
        var coupon = await _db.Coupons
            .FirstOrDefaultAsync(c => c.Code == req.Code && c.IsActive);

        if (coupon == null)
            return BadRequest(new { message = "كود الخصم غير صحيح أو غير مفعل" });

        if (coupon.ExpiresAt.HasValue && coupon.ExpiresAt < now)
            return BadRequest(new { message = "عذراً، هذا الكوبون انتهت صلاحيته" });

        // منع إعادة استخدام الكوبون من نفس المستخدم
        var alreadyUsed = await _db.UserCoupons.AnyAsync(uc => uc.UserId == userId && uc.CouponId == coupon.Id);
        if (alreadyUsed)
            return BadRequest(new { message = "لقد قمت باستخدام هذا الكوبون من قبل" });

        if (coupon.MinOrderAmount.HasValue && req.OrderAmount < coupon.MinOrderAmount)
            return BadRequest(new { message = $"الحد الأدنى للطلب {coupon.MinOrderAmount} جنيه" });

        if (coupon.UsageLimit.HasValue && coupon.UsedCount >= coupon.UsageLimit)
            return BadRequest(new { message = "هذا الكود وصل للحد الأقصى من الاستخدام" });

        decimal discount;
        if (coupon.DiscountType == "Percentage")
        {
            discount = req.OrderAmount * (coupon.DiscountValue / 100);
            if (coupon.MaxDiscount.HasValue && discount > coupon.MaxDiscount)
                discount = coupon.MaxDiscount.Value;
        }
        else
        {
            discount = coupon.DiscountValue;
        }

        return Ok(new
        {
            coupon.Id,
            coupon.Code,
            coupon.Title,
            Discount = discount,
            FinalAmount = req.OrderAmount - discount
        });
    }

    [Authorize]
    [HttpGet("my")]
    public async Task<IActionResult> GetMyCoupons()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        var userId = int.Parse(userIdClaim?.Value!);
        
        var now = DateTime.UtcNow;
        var allCoupons = await _db.Coupons.Where(c => c.IsActive).ToListAsync();
        var usedCouponIds = await _db.UserCoupons.Where(uc => uc.UserId == userId).Select(uc => uc.CouponId).ToListAsync();

        var result = allCoupons.Select(c => new
        {
            c.Id,
            c.Code,
            c.Title,
            c.Description,
            c.DiscountType,
            c.DiscountValue,
            c.MinOrderAmount,
            c.MaxDiscount,
            c.ExpiresAt,
            Status = usedCouponIds.Contains(c.Id) ? "Used" : 
                     (c.ExpiresAt.HasValue && c.ExpiresAt < now) ? "Expired" : "Available",
            ExpiresInDays = c.ExpiresAt.HasValue ? (int?)(c.ExpiresAt.Value - now).TotalDays : null
        });

        return Ok(result);
    }

    // POST api/coupons  — admin: create coupon
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] Coupon coupon)
    {
        coupon.CreatedAt = DateTime.UtcNow;
        coupon.UsedCount = 0;
        _db.Coupons.Add(coupon);
        await _db.SaveChangesAsync();
        return Ok(coupon);
    }

    // DELETE api/coupons/{id}
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Coupons.FindAsync(id);
        if (c == null) return NotFound();
        _db.Coupons.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted" });
    }
}

public class ValidateCouponRequest
{
    public string Code { get; set; } = string.Empty;
    public decimal OrderAmount { get; set; }
}
