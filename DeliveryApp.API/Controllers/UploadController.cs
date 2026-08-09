using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UploadController : ControllerBase
{
    private readonly IImageStorageService _storage;
    private readonly ILogger<UploadController> _logger;

    public UploadController(IImageStorageService storage, ILogger<UploadController> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    [HttpPost("prescription")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadPrescription(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "No file uploaded" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".pdf")
            return BadRequest(new { message = "Allowed: jpg, png, webp, pdf" });

        var fileName = $"{Guid.NewGuid():N}{ext}";

        try
        {
            await using var stream = file.OpenReadStream();
            // ✅ بيترفع على Cloudinary بدل الديسك المحلي، عشان ميتمسحش
            // لما السيرفر يعمل Recycle أو تتعمل Publish جديدة (wwwroot بيتستبدل بالكامل وقت الـ deploy)
            var url = await _storage.UploadFileAsync(stream, fileName, file.ContentType, "prescriptions");
            return Ok(new { url, fileName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UploadController] Failed to upload prescription to Cloudinary");
            // ⚠️ مؤقتًا بنرجّع تفاصيل الخطأ الحقيقي عشان نشخّص المشكلة.
            // لازم نشيل ex.Message من الـ response ده قبل ما نثبّت الحل نهائيًا،
            // عشان متسربش تفاصيل داخلية للعميل في بيئة الإنتاج.
            return StatusCode(500, new { message = "فشل رفع الصورة", error = ex.Message });
        }
    }
}
