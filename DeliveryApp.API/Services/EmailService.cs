using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
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
    //       "SenderName": "Tawseela",
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

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendOtpEmailAsync(string toEmail, string code, string purpose)
        {
            var senderEmail = _config["Email:SenderEmail"];
            var senderName = _config["Email:SenderName"] ?? "Tawseela";
            var appPassword = _config["Email:AppPassword"];

            if (string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(appPassword))
                throw new InvalidOperationException("Email sender settings are not configured in appsettings.json (Email:SenderEmail / Email:AppPassword).");

            var isRegister = purpose == "Register";
            var subject = isRegister ? "كود تفعيل حسابك في Tawseela" : "كود استعادة كلمة المرور - Tawseela";
            var actionText = isRegister ? "لتفعيل حسابك" : "لاستعادة كلمة المرور";

            var body = $@"
                <div style='font-family:Tahoma,Arial,sans-serif;direction:rtl;text-align:right;background:#F8F9FA;padding:24px'>
                    <div style='max-width:420px;margin:auto;background:white;border-radius:16px;padding:24px;border:1px solid #eee'>
                        <h2 style='color:#FF5722;margin-bottom:4px'>Tawseela</h2>
                        <p style='color:#212121;font-size:15px'>كود التحقق بتاعك {actionText}:</p>
                        <div style='background:#FFF3E0;color:#FF5722;font-size:32px;font-weight:bold;letter-spacing:6px;text-align:center;padding:16px;border-radius:12px;margin:16px 0'>
                            {code}
                        </div>
                        <p style='color:#757575;font-size:13px'>الكود صالح لمدة 5 دقايق فقط. لو مطلبتوش أنت، تجاهل الرسالة دي.</p>
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