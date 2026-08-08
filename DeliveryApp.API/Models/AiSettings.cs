using System.ComponentModel.DataAnnotations;

namespace DeliveryApp.API.Models;

// ─────────────────────────────────────────────────────────────────────────
// إعدادات الـ AI بتاع شات الدعم — صف واحد بس (Id = 1 دايمًا) بيتحدث من
// شاشة الأدمن. الـ ApiKey بتاعة OpenRouter بقت متخزنة هنا مش هاردكودد في
// تطبيق الكاستمر زي الأول، عشان الأدمن يقدر يغيرها/يوقف الـ AI من غير
// ما يعمل ريليز جديد للأبلكيشن.
// ─────────────────────────────────────────────────────────────────────────
public class AiSettings
{
    [Key]
    public int Id { get; set; }

    public bool IsEnabled { get; set; } = true;

    [StringLength(300)]
    public string? ApiKey { get; set; }

    [StringLength(100)]
    public string Model { get; set; } = "openai/gpt-4o-mini";

    public string? SystemPrompt { get; set; }

    public int MaxTokens { get; set; } = 512;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
