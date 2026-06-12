
using DeliveryApp.API.Models;
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

    // GET api/deals  — returns active deals (for rewards screen)
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
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

        return Ok(deals);
    }

    // GET api/deals/by-restaurant/{restaurantId}
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
