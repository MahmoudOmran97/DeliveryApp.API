using DeliveryApp.API.DTOs.Auth;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IJwtService _jwtService;
        private readonly IOtpService _otpService;

        private const string PurposeRegister = "Register";
        private const string PurposeReset = "ResetPassword";

        public AuthController(
            ApplicationDbContext context,
            IJwtService jwtService,
            IOtpService otpService)
        {
            _context = context;
            _jwtService = jwtService;
            _otpService = otpService;
        }

        // ─────────────────────────────────────────────────────────
        // ✅ الجديد: إرسال كود OTP (لتسجيل حساب جديد أو نسيت كلمة المرور)
        // POST api/auth/send-otp   { email, purpose: "Register" | "ResetPassword" }
        // ─────────────────────────────────────────────────────────
        [HttpPost("send-otp")]
        public async Task<IActionResult> SendOtp(SendOtpDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Purpose))
                return BadRequest(new { message = "البريد الإلكتروني والغرض مطلوبين" });

            if (dto.Purpose != PurposeRegister && dto.Purpose != PurposeReset)
                return BadRequest(new { message = "قيمة purpose غير صحيحة" });

            var exists = await _context.Users.AnyAsync(x => x.Email == dto.Email);

            if (dto.Purpose == PurposeRegister && exists)
                return BadRequest(new { message = "البريد الإلكتروني مسجل بالفعل" });

            if (dto.Purpose == PurposeReset && !exists)
                return BadRequest(new { message = "لا يوجد حساب مسجل بهذا البريد الإلكتروني" });

            try
            {
                await _otpService.GenerateAndSendAsync(dto.Email, dto.Purpose);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }

            return Ok(new { message = "تم إرسال كود التحقق على بريدك الإلكتروني" });
        }

        // ─────────────────────────────────────────────────────────
        // ✅ الجديد: تحقق من الكود بس (من غير ما يستهلكه) — بيستخدم
        // في واجهة التطبيق قبل ما ننتقل لخطوة إدخال باقي البيانات
        // POST api/auth/verify-otp   { email, code, purpose }
        // ─────────────────────────────────────────────────────────
        [HttpPost("verify-otp")]
        public async Task<IActionResult> VerifyOtp(VerifyOtpDto dto)
        {
            var valid = await _otpService.ValidateAsync(dto.Email, dto.Code, dto.Purpose);

            if (!valid)
                return BadRequest(new { message = "الكود غير صحيح أو منتهي الصلاحية" });

            return Ok(new { message = "تم التحقق من الكود بنجاح" });
        }

        // ─────────────────────────────────────────────────────────
        // ✅ الجديد: تغيير كلمة المرور بعد التحقق من كود الـ OTP
        // POST api/auth/reset-password   { email, code, newPassword }
        // ─────────────────────────────────────────────────────────
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword(ResetPasswordDto dto)
        {
            var valid = await _otpService.ValidateAndConsumeAsync(dto.Email, dto.Code, PurposeReset);

            if (!valid)
                return BadRequest(new { message = "الكود غير صحيح أو منتهي الصلاحية" });

            var user = await _context.Users.FirstOrDefaultAsync(x => x.Email == dto.Email);
            if (user == null)
                return BadRequest(new { message = "الحساب غير موجود" });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            await _context.SaveChangesAsync();

            return Ok(new { message = "تم تغيير كلمة المرور بنجاح" });
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterDto dto)
        {
            var exists = await _context.Users
                .AnyAsync(x => x.Email == dto.Email);

            if (exists)
            {
                return BadRequest(new
                {
                    message = "Email already exists"
                });
            }

            // ✅ الجديد: تحقق من كود الـ OTP واستهلكه قبل ما نعمل الحساب فعليًا
            var otpValid = await _otpService.ValidateAndConsumeAsync(dto.Email, dto.Otp, PurposeRegister);
            if (!otpValid)
            {
                return BadRequest(new
                {
                    message = "كود التحقق غير صحيح أو منتهي الصلاحية"
                });
            }

            var user = new User
            {
                FullName = dto.FullName,
                Email = dto.Email,
                Phone = dto.Phone,
                Role = dto.Role,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);

            await _context.SaveChangesAsync();

            var token = _jwtService.GenerateToken(user);

            return Ok(new
            {
                token,
                user.Id,
                user.FullName,
                user.Email,
                user.Role
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(x => x.Email == dto.Email);

            if (user == null)
            {
                return Unauthorized(new
                {
                    message = "Invalid email or password"
                });
            }

            var validPassword = BCrypt.Net.BCrypt.Verify(
                dto.Password,
                user.PasswordHash);

            if (!validPassword)
            {
                return Unauthorized(new
                {
                    message = "Invalid email or password"
                });
            }

            var token = _jwtService.GenerateToken(user);

            return Ok(new
            {
                token,
                user.Id,
                user.FullName,
                user.Email,
                user.Role
            });
        }
        [HttpPost("restaurant-login")]
        public async Task<IActionResult> RestaurantLogin(LoginDto dto)
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(x => x.Email == dto.Email);

            if (user == null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
                return Unauthorized(new { message = "البريد الإلكتروني أو كلمة المرور غير صحيحة" });

            if (!user.IsActive)
                return Unauthorized(new { message = "الحساب موقوف، تواصل مع الإدارة" });

            // ✅ تأكد إن الدور Restaurant أو Admin بس
            if (user.Role != "Restaurant" && user.Role != "Admin")
                return StatusCode(403, new { message = "هذا التطبيق مخصص لأصحاب المطاعم فقط" });

            // جيب الـ restaurantId بتاعه
            var restaurantId = await _context.Restaurants
                .Where(r => r.OwnerUserId == user.Id && r.IsActive)
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (restaurantId == 0 && user.Role != "Admin")
                return StatusCode(403, new { message = "لا يوجد مطعم مرتبط بهذا الحساب، تواصل مع الإدارة" });

            var token = _jwtService.GenerateToken(user);

            return Ok(new
            {
                token,
                user.Id,
                user.FullName,
                user.Email,
                user.Role,
                restaurantId   // ← الجديد: MAUI بيستخدمه مباشرة بدل ما اليوزر يكتبه
            });
        }
    }
}