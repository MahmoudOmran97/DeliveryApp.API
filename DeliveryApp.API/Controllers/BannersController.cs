
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BannersController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public BannersController(ApplicationDbContext db) => _db = db;

    // GET api/banners  — public, returns active banners sorted by order
    // ✅ FIX: بيقبل دلوقتي lat/lng/radiusKm. ActionUrl بتاع البانر ممكن يكون
    // "restaurant/{id}" أو "store/{id}" (زي ما الموبايل بيفكه في OpenBanner) —
    // لو كان كده، بنستبعد البانر لو المحل ده برا نطاق التوصيل (الزون) الحالي.
    // بانرات categories أو لينكات خارجية أو من غير ActionUrl بتفضل تظهر دايمًا.
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll(
        [FromQuery] double? lat,
        [FromQuery] double? lng,
        [FromQuery] double radiusKm = 10.0)
    {
        var now = DateTime.UtcNow;
        var banners = await _db.Banners
            .Where(b => b.IsActive
                && (b.StartsAt == null || b.StartsAt <= now)
                && (b.EndsAt == null || b.EndsAt >= now))
            .OrderBy(b => b.SortOrder)
            .Select(b => new
            {
                b.Id,
                b.Title,
                b.SubTitle,
                b.ImageUrl,
                b.ActionUrl,
                b.BackgroundColor,
                b.SortOrder
            })
            .ToListAsync();

        bool useLocation = lat.HasValue && lng.HasValue;
        if (useLocation && banners.Count > 0)
        {
            var (maxZoneKm, _) = await DeliveryFeeCalculator.GetZoneSettingsAsync(_db);
            radiusKm = Math.Min(radiusKm, maxZoneKm);

            // نلاقط كل الـ restaurantId المذكورة في ActionUrl عشان نجيبهم بضربة واحدة من الداتابيز
            var restaurantIds = new List<int>();
            foreach (var b in banners)
            {
                if (TryGetRestaurantIdFromActionUrl(b.ActionUrl, out var rid))
                    restaurantIds.Add(rid);
            }

            var restaurantLocations = restaurantIds.Count == 0
                ? new Dictionary<int, (double Latitude, double Longitude)>()
                : await _db.Restaurants
                    .Where(r => restaurantIds.Contains(r.Id))
                    .Select(r => new { r.Id, r.Latitude, r.Longitude })
                    .ToDictionaryAsync(r => r.Id, r => (r.Latitude, r.Longitude));

            banners = banners
                .Where(b =>
                {
                    if (!TryGetRestaurantIdFromActionUrl(b.ActionUrl, out var rid))
                        return true; // مش مرتبط بمحل معين → دايمًا يظهر

                    if (!restaurantLocations.TryGetValue(rid, out var loc))
                        return true; // المحل مش موجود/اتشال → سيبها للموبايل يتعامل معاها (رابط هيفشل بهدوء)

                    return DeliveryFeeCalculator.GetDistanceKm(lat!.Value, lng!.Value, loc.Latitude, loc.Longitude) <= radiusKm;
                })
                .ToList();
        }

        return Ok(banners);
    }

    // بيفك ActionUrl بصيغة "restaurant/5" أو "store/5" ويرجع الـ Id
    private static bool TryGetRestaurantIdFromActionUrl(string? actionUrl, out int restaurantId)
    {
        restaurantId = 0;
        if (string.IsNullOrWhiteSpace(actionUrl)) return false;
        if (actionUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = actionUrl.Trim().Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var type = parts[0].ToLowerInvariant();
        if (type != "restaurant" && type != "store") return false;

        return int.TryParse(parts[1], out restaurantId);
    }

    // GET api/banners/admin — كل البانرات (نشطة وغير نشطة) للوحة الإدارة، بدون فلترة زون
    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllAdmin()
    {
        var banners = await _db.Banners
            .OrderBy(b => b.SortOrder)
            .Select(b => new
            {
                b.Id,
                b.Title,
                b.SubTitle,
                b.ImageUrl,
                b.ActionUrl,
                b.BackgroundColor,
                b.SortOrder,
                b.IsActive,
                b.StartsAt,
                b.EndsAt,
                b.CreatedAt
            })
            .ToListAsync();

        return Ok(banners);
    }

    // POST api/banners — admin only
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] Banner banner)
    {
        banner.CreatedAt = DateTime.UtcNow;
        _db.Banners.Add(banner);
        await _db.SaveChangesAsync();
        return Ok(banner);
    }

    // PUT api/banners/{id} — admin only
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int id, [FromBody] Banner updated)
    {
        var b = await _db.Banners.FindAsync(id);
        if (b == null) return NotFound();
        b.Title = updated.Title;
        b.SubTitle = updated.SubTitle;
        b.ImageUrl = updated.ImageUrl;
        b.ActionUrl = updated.ActionUrl;
        b.BackgroundColor = updated.BackgroundColor;
        b.SortOrder = updated.SortOrder;
        b.IsActive = updated.IsActive;
        b.StartsAt = updated.StartsAt;
        b.EndsAt = updated.EndsAt;
        await _db.SaveChangesAsync();
        return Ok(b);
    }

    // DELETE api/banners/{id} — admin only
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await _db.Banners.FindAsync(id);
        if (b == null) return NotFound();
        _db.Banners.Remove(b);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Deleted" });
    }
}
