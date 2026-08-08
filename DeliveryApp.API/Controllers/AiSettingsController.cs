using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class AiSettingsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    public AiSettingsController(ApplicationDbContext context) => _context = context;

    // GET api/aisettings
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _context.AiSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AiSettings();
            _context.AiSettings.Add(settings);
            await _context.SaveChangesAsync();
        }

        return Ok(new
        {
            settings.Id,
            settings.IsEnabled,
            // الـ API Key ما بيرجعش كامل للفرونت (بيتعرض ماسك) عشان الأمان
            ApiKeyMasked = MaskKey(settings.ApiKey),
            HasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey),
            settings.Model,
            settings.SystemPrompt,
            settings.MaxTokens,
            settings.UpdatedAt
        });
    }

    // PUT api/aisettings
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateAiSettingsDto dto)
    {
        var settings = await _context.AiSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AiSettings();
            _context.AiSettings.Add(settings);
        }

        settings.IsEnabled = dto.IsEnabled;
        // لو الأدمن سايب الحقل فاضي، سيبي المفتاح القديم زي ما هو (عشان ميتمسحش بالغلط)
        if (!string.IsNullOrWhiteSpace(dto.ApiKey))
            settings.ApiKey = dto.ApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(dto.Model))
            settings.Model = dto.Model.Trim();
        settings.SystemPrompt = dto.SystemPrompt;
        if (dto.MaxTokens is > 0)
            settings.MaxTokens = dto.MaxTokens.Value;
        settings.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { message = "تم حفظ إعدادات الـ AI بنجاح" });
    }

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length <= 8) return "****";
        return key[..6] + "..." + key[^4..];
    }
}

public class UpdateAiSettingsDto
{
    public bool IsEnabled { get; set; } = true;
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public int? MaxTokens { get; set; }
}
