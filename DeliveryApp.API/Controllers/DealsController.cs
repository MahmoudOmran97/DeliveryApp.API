
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DealsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private const string ImgBase = "https://deliveryappapi.runasp.net";

    public DealsController(ApplicationDbContext db) => _db = db;

    // GET api/deals  — returns active deals (for rewards + home screens)
    // ✅ FIX: بيقبل دلوقتي lat/lng/radiusKm زي /api/restaurants بالظبط، وبيفلتر
    // العروض المرتبطة بمحل (RestaurantId) بحيث ميظهرش عرض لمحل برا نطاق التوصيل
    // (الزون) الحالي بتاع العميل. العروض العامة (RestaurantId == null) بتفضل تظهر دايمًا.
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] double? lat,
        [FromQuery] double? lng,
        [FromQuery] double radiusKm = 10.0)
    {
        var now = DateTime.UtcNow;
        var deals = await _db.Deals
            .Include(d => d.Restaurant)
            .Include(d => d.Product)
            .Where(d => d.IsActive && (d.ExpiresAt == null || d.ExpiresAt >= now))
            .OrderBy(d => d.SortOrder)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.Description,
                ImageUrl = d.ImageUrl != null
                    ? (d.ImageUrl.StartsWith("http") ? d.ImageUrl : ImgBase + "/" + d.ImageUrl.TrimStart('/'))
                    : (d.Product != null && d.Product.ImageUrl != null
                        ? (d.Product.ImageUrl.StartsWith("http") ? d.Product.ImageUrl : ImgBase + "/" + d.Product.ImageUrl.TrimStart('/'))
                        : null),
                d.RestaurantId,
                RestaurantName = d.Restaurant != null ? d.Restaurant.Name : null,
                RestaurantImage = d.Restaurant != null && d.Restaurant.ImageUrl != null
                    ? (d.Restaurant.ImageUrl.StartsWith("http") ? d.Restaurant.ImageUrl : ImgBase + "/" + d.Restaurant.ImageUrl.TrimStart('/'))
                    : null,
                RestaurantLatitude = d.Restaurant != null ? d.Restaurant.Latitude : (double?)null,
                RestaurantLongitude = d.Restaurant != null ? d.Restaurant.Longitude : (double?)null,
                RestaurantIsActive = d.Restaurant != null ? d.Restaurant.IsActive : true,
                d.ProductId,
                ProductName = d.Product != null ? d.Product.Name : null,
                d.OriginalPrice,
                d.DiscountedPrice,
                d.DiscountPercent,
                d.BadgeText,
                d.BadgeColor,
                d.ExpiresAt,
                d.SortOrder
            })
            .ToListAsync();

        // ── فلترة الزون (نفس منطق /api/restaurants بالظبط) ──────────────────────
        bool useLocation = lat.HasValue && lng.HasValue;
        if (useLocation)
        {
            var (maxZoneKm, _) = await DeliveryFeeCalculator.GetZoneSettingsAsync(_db);
            radiusKm = Math.Min(radiusKm, maxZoneKm);

            deals = deals
                .Where(d =>
                    // عرض عام (مش مرتبط بمحل) → يفضل يظهر دايمًا
                    !d.RestaurantId.HasValue
                    // محل اتقفل/اتشال ← منستبعدوش هنا (شغل الـ IsActive على مستوى العرض نفسه فوق)
                    || !d.RestaurantLatitude.HasValue || !d.RestaurantLongitude.HasValue
                    // أو المحل جوه نطاق التوصيل الحالي
                    || DeliveryFeeCalculator.GetDistanceKm(
                           lat!.Value, lng!.Value,
                           d.RestaurantLatitude.Value, d.RestaurantLongitude.Value) <= radiusKm)
                .ToList();
        }

        // ما نرجعش إحداثيات المحل للكلاينت، مش محتاجها هناك
        var result = deals.Select(d => new
        {
            d.Id,
            d.Title,
            d.Description,
            d.ImageUrl,
            d.RestaurantId,
            d.RestaurantName,
            d.RestaurantImage,
            d.ProductId,
            d.ProductName,
            d.OriginalPrice,
            d.DiscountedPrice,
            d.DiscountPercent,
            d.BadgeText,
            d.BadgeColor,
            d.ExpiresAt,
            d.SortOrder
        });

        return Ok(result);
    }

    // GET api/deals/admin — كل العروض (نشطة وغير نشطة ومنتهية) للوحة الإدارة
    // بدون فلترة زون عمدًا: الأدمن لازم يشوف ويدير كل العروض بغض النظر عن موقع أي حد.
    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAdmin()
    {
        var now = DateTime.UtcNow;
        var deals = await _db.Deals
            .Include(d => d.Restaurant)
            .Include(d => d.Product)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.Description,
                d.ImageUrl,
                d.RestaurantId,
                RestaurantName = d.Restaurant != null ? d.Restaurant.Name : null,
                d.ProductId,
                ProductName = d.Product != null ? d.Product.Name : null,
                d.OriginalPrice,
                d.DiscountedPrice,
                d.DiscountPercent,
                d.BadgeText,
                d.BadgeColor,
                d.IsActive,
                d.SortOrder,
                d.ExpiresAt,
                d.CreatedAt,
                IsExpired = d.ExpiresAt.HasValue && d.ExpiresAt < now
            })
            .ToListAsync();

        return Ok(deals);
    }

    // GET api/deals/by-restaurant/{restaurantId}  — العميل أصلاً فاتح المحل ده، فمفيش لازمة لفلترة زون هنا
    [HttpGet("by-restaurant/{restaurantId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByRestaurant(int restaurantId)
    {
        var now = DateTime.UtcNow;
        var deals = await _db.Deals
            .Include(d => d.Product)
            .Where(d => d.RestaurantId == restaurantId
                && d.IsActive
                && (d.ExpiresAt == null || d.ExpiresAt >= now))
            .OrderBy(d => d.SortOrder)
            .ToListAsync();

        return Ok(deals);
    }

    // POST api/deals  — admin: create deal
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] Deal deal)
    {
        deal.CreatedAt = DateTime.UtcNow;
        _db.Deals.Add(deal);
        await _db.SaveChangesAsync();
        return Ok(deal);
    }

    // PUT api/deals/{id}
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] Deal updated)
    {
        var d = await _db.Deals.FindAsync(id);
        if (d == null) return NotFound();
        d.Title = updated.Title;
        d.Description = updated.Description;
        d.ImageUrl = updated.ImageUrl;
        d.RestaurantId = updated.RestaurantId;
        d.ProductId = updated.ProductId;
        d.OriginalPrice = updated.OriginalPrice;
        d.DiscountedPrice = updated.DiscountedPrice;
        d.DiscountPercent = updated.DiscountPercent;
        d.BadgeText = updated.BadgeText;
        d.BadgeColor = updated.BadgeColor;
        d.IsActive = updated.IsActive;
        d.SortOrder = updated.SortOrder;
        d.ExpiresAt = updated.ExpiresAt;
        await _db.SaveChangesAsync();
        return Ok(d);
    }

    // DELETE api/deals/{id}
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var d = await _db.Deals.FindAsync(id);
        if (d == null) return NotFound();
        _db.Deals.Remove(d);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted" });
    }
}
