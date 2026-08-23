using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DeliveryApp.API.Services
{
    // ─────────────────────────────────────────────────────────────
    // بيستخدم Gmail SMTP لإرسال كود التحقق (OTP) على الإيميل.
    // بيعتمد على System.Net.Mail المدمج في .NET، فمفيش داعي
    // لإضافة أي NuGet package جديد.
    //
    // إعدادات appsettings.json المطلوبة (قسم "Email"):
    //   "Email": {
    //       "SenderEmail": "youraccount@gmail.com",
    //       "SenderName": "Taly",
    //       "AppPassword": "xxxx xxxx xxxx xxxx"   ← App Password مش كلمة مرور الحساب العادية
    //   }
    //
    // ⚠️ إزاي تجيب App Password من Gmail:
    // 1) فعّل "2-Step Verification" على حساب الجيميل بتاعك من myaccount.google.com/security
    // 2) روح myaccount.google.com/apppasswords
    // 3) اختار "Mail" كنوع التطبيق، وهيديك كود 16 حرف — ده اللي تحطه في AppPassword
    // ─────────────────────────────────────────────────────────────
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailService> _logger;
        private readonly ApplicationDbContext _db;

        public EmailService(IConfiguration config, ILogger<EmailService> logger, ApplicationDbContext db)
        {
            _config = config;
            _logger = logger;
            _db = db;
        }

        // ✅ الجديد: إيموجي بسيط لكل نوع رابط (بدل الاعتماد على صور مستضافة برا،
        // اللي كتير من عملاء الإيميل بيحجبها افتراضيًا). Key بييجي من جدول SiteLinks
        // (website, facebook, instagram, tiktok, x, ...).
        private static string IconFor(string key) => key.ToLowerInvariant() switch
        {
            "website" => "🌐",
            "facebook" => "📘",
            "instagram" => "📸",
            "tiktok" => "🎵",
            "x" or "twitter" => "✖️",
            "whatsapp" => "💬",
            "youtube" => "▶️",
            _ => "🔗"
        };

        public async Task SendOtpEmailAsync(string toEmail, string code, string purpose)
        {
            var senderEmail = _config["Email:SenderEmail"];
            var senderName = _config["Email:SenderName"] ?? "Taly";
            var appPassword = _config["Email:AppPassword"];
            // ✅ اسم التطبيق اللي بيظهر فوق في الإيميل (عربي/إنجليزي) — قابل للتعديل من
            // appsettings.json (Email:AppDisplayName) من غير ما تلمس الكود. افتراضي: "Taly | تالي"
            var appDisplayName = _config["Email:AppDisplayName"] ?? "Taly | تالي";

            if (string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(appPassword))
                throw new InvalidOperationException("Email sender settings are not configured in appsettings.json (Email:SenderEmail / Email:AppPassword).");

            var isRegister = purpose == "Register";
            var subject = isRegister
                ? $"{appDisplayName} | كود تفعيل الحساب - Account Verification Code"
                : $"{appDisplayName} | كود استعادة كلمة المرور - Password Reset Code";

            var actionTextAr = isRegister ? "لتفعيل حسابك" : "لاستعادة كلمة المرور";
            var actionTextEn = isRegister ? "to verify your account" : "to reset your password";

            // ✅ الجديد: روابط مواقع التواصل + الموقع بتاعت التطبيق — نفس البيانات
            // المتخزنة في جدول SiteLinks واللي بتظهر في صفحة "عن التطبيق" بتطبيق العميل
            var siteLinks = await _db.SiteLinks
                .AsNoTracking()
                .Where(x => x.IsActive && x.Url != "")
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Key)
                .ToListAsync();

            var socialLinksHtml = siteLinks.Count == 0
                ? ""
                : $@"<div style='text-align:center;margin-top:24px;padding-top:20px;border-top:1px solid #eee'>
                        {string.Join("", siteLinks.Select(l => $@"
                        <a href='{l.Url}' style='display:inline-block;margin:0 8px;text-decoration:none;font-size:22px' target='_blank'>{IconFor(l.Key)}</a>"))}
                    </div>";

            var body = $@"
                <div style='font-family:Tahoma,Arial,sans-serif;background:#F8F9FA;padding:24px'>
                    <div style='max-width:460px;margin:auto;background:#ffffff;border-radius:16px;padding:0;border:1px solid #eee;overflow:hidden'>

                        <!-- ── Header: اسم التطبيق ───────────────────────────── -->
                        <div style='background:#FF5722;padding:20px;text-align:center'>
                            <h1 style='color:#ffffff;margin:0;font-size:22px;font-family:Tahoma,Arial,sans-serif'>{appDisplayName}</h1>
                        </div>

                        <div style='padding:28px 24px'>

                            <!-- ── القسم العربي ─────────────────────────────── -->
                            <div style='direction:rtl;text-align:right;margin-bottom:20px'>
                                <p style='color:#212121;font-size:15px;margin:0 0 4px'>أهلاً بيك في {appDisplayName} 👋</p>
                                <p style='color:#212121;font-size:15px;margin:0'>كود التحقق بتاعك {actionTextAr}:</p>
                            </div>

                            <!-- ── الكود ─────────────────────────────────────── -->
                            <div style='background:#FFF3E0;color:#FF5722;font-size:34px;font-weight:bold;letter-spacing:8px;text-align:center;padding:18px;border-radius:12px;margin:8px 0 20px;font-family:Tahoma,Arial,sans-serif'>
                                {code}
                            </div>

                            <!-- ── القسم الإنجليزي ──────────────────────────── -->
                            <div style='direction:ltr;text-align:left;border-top:1px solid #eee;padding-top:16px'>
                                <p style='color:#212121;font-size:15px;margin:0 0 4px'>Welcome to {appDisplayName} 👋</p>
                                <p style='color:#212121;font-size:15px;margin:0'>Your verification code {actionTextEn}:</p>
                            </div>

                            <hr style='border:none;border-top:1px solid #eee;margin:20px 0'/>

                            <p style='direction:rtl;text-align:right;color:#757575;font-size:13px;margin:0 0 6px'>الكود صالح لمدة 5 دقايق فقط. لو مطلبتوش أنت، تجاهل الرسالة دي.</p>
                            <p style='direction:ltr;text-align:left;color:#757575;font-size:13px;margin:0'>This code is valid for 5 minutes only. If you didn't request it, please ignore this email.</p>

                            {socialLinksHtml}
                        </div>
                    </div>
                </div>";

            using var message = new MailMessage
            {
                From = new MailAddress(senderEmail, senderName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true,
                SubjectEncoding = System.Text.Encoding.UTF8,
                BodyEncoding = System.Text.Encoding.UTF8
            };
            message.To.Add(toEmail);

            using var client = new SmtpClient("smtp.gmail.com", 587)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(senderEmail, appPassword)
            };

            try
            {
                await client.SendMailAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send OTP email to {Email}", toEmail);
                throw new InvalidOperationException("Failed to send verification email. Please try again.");
            }
        }
    }
}