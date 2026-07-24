using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeliverySettingsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    public DeliverySettingsController(ApplicationDbContext context) => _context = context;

    // ─── GET /api/deliverysettings  (public) ────────────────────────────────
    // متاح للجميع بدون تسجيل دخول، عشان أي واجهة (تطبيق العميل مثلاً) تقدر
    // تعرض تفاصيل حساب سعر التوصيل لو احتاجت. القيم نفسها مش حساسة.
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get()
    {
        var settings = await _context.DeliverySettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new DeliverySettings();
            _context.DeliverySettings.Add(settings);
            await _context.SaveChangesAsync();
        }

        return Ok(new
        {
            settings.Id,
            settings.FreeRadiusKm,
            settings.ExtraFeePerKm,
            settings.UpdatedAt
        });
    }

    // ─── PUT /api/deliverysettings  (Admin only) ────────────────────────────
    [HttpPut]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update([FromBody] UpdateDeliverySettingsDto dto)
    {
        if (dto.FreeRadiusKm < 0)
            return BadRequest(new { message = "FreeRadiusKm must be >= 0" });
        if (dto.ExtraFeePerKm < 0)
            return BadRequest(new { message = "ExtraFeePerKm must be >= 0" });

        var settings = await _context.DeliverySettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new DeliverySettings();
            _context.DeliverySettings.Add(settings);
        }

        settings.FreeRadiusKm = dto.FreeRadiusKm;
        settings.ExtraFeePerKm = dto.ExtraFeePerKm;
        settings.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new
        {
            message = "Delivery settings updated",
            settings.Id,
            settings.FreeRadiusKm,
            settings.ExtraFeePerKm,
            settings.UpdatedAt
        });
    }
}

public class UpdateDeliverySettingsDto
{
    public double FreeRadiusKm { get; set; }
    public decimal ExtraFeePerKm { get; set; }
}
