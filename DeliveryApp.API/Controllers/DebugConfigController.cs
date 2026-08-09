using Microsoft.AspNetCore.Mvc;

namespace DeliveryApp.API.Controllers;

// ⚠️ Controller تشخيصي مؤقت فقط — لازم يتشال بعد ما نحل مشكلة الـ Cloudinary config.
// بيوريك القيمة الفعلية اللي الـ IConfiguration شايفها لقسم Cloudinary،
// عشان نعرف مصدر القيمة الغلط جاي منين (appsettings / env vars / user secrets / إلخ).
[ApiController]
[Route("api/[controller]")]
public class DebugConfigController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public DebugConfigController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    [HttpGet("cloudinary")]
    public IActionResult GetCloudinaryConfig()
    {
        var cloudName = _config["Cloudinary:CloudName"] ?? "(null)";
        var apiKey = _config["Cloudinary:ApiKey"] ?? "(null)";
        var apiSecret = _config["Cloudinary:ApiSecret"] ?? "(null)";

        return Ok(new
        {
            EnvironmentName = _env.EnvironmentName,
            ContentRootPath = _env.ContentRootPath,
            CloudName = cloudName,
            ApiKeyFull = apiKey,
            ApiKeyLength = apiKey.Length,
            ApiSecretLength = apiSecret.Length,
            ApiSecretPreview = apiSecret.Length > 4 ? apiSecret[..4] + "..." : apiSecret
        });
    }
}
