using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

// ─────────────────────────────────────────────────────────────
// جدول تخزين أكواد الـ OTP (تسجيل حساب جديد / نسيت كلمة المرور)
// الجدول بيتعمل تلقائي عند تشغيل الـ API لأول مرة (Program.cs)
// مفيش داعي لعمل Migration يدوي.
// ─────────────────────────────────────────────────────────────
public class OtpCode
{
    [Key]
    public int Id { get; set; }

    [StringLength(150)]
    public string Email { get; set; } = null!;

    [StringLength(10)]
    public string Code { get; set; } = null!;

    // "Register" أو "ResetPassword"
    [StringLength(30)]
    public string Purpose { get; set; } = null!;

    public bool IsUsed { get; set; } = false;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}