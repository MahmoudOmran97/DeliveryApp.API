
using DeliveryApp.API.Models;
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
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAll()
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
