using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public UserController(ApplicationDbContext context) => _context = context;

        private int GetUserId()
        {
            var claim = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier)
                     ?? User.Claims.FirstOrDefault(x => x.Type == "sub");
            return Convert.ToInt32(claim?.Value);
        }

        // GET api/user/me
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == GetUserId());
            if (user == null) return NotFound();

            return Ok(new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.Phone,
                user.Role,
                user.Address,
                user.ProfileImageUrl,
                user.CreatedAt
            });
        }

        // PUT api/user/me  — تعديل البيانات الشخصية
        [HttpPut("me")]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == GetUserId());
            if (user == null) return NotFound();

            user.FullName = dto.FullName ?? user.FullName;
            user.Phone = dto.Phone ?? user.Phone;
            user.Address = dto.Address ?? user.Address;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Profile updated", user.FullName, user.Phone, user.Address });
        }

        // PUT api/user/change-password
        [HttpPut("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == GetUserId());
            if (user == null) return NotFound();

            if (!BCrypt.Net.BCrypt.Verify(dto.OldPassword, user.PasswordHash))
                return BadRequest(new { message = "Old password is incorrect" });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Password changed successfully" });
        }

        // PUT api/user/fcm-token  — تحديث توكن النوتيفيكيشنز
        [HttpPut("fcm-token")]
        public async Task<IActionResult> UpdateFcmToken([FromBody] UpdateFcmDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == GetUserId());
            if (user == null) return NotFound();

            user.Fcmtoken = dto.Token;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "FCM token updated" });
        }

        // DELETE api/user/me  — حذف الحساب
        [HttpDelete("me")]
        public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountDto dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(x => x.Id == GetUserId());
            if (user == null) return NotFound();

            if (!BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
                return BadRequest(new { message = "Incorrect password" });

            user.IsActive = false;  // Soft delete
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Account deactivated successfully" });
        }
    }

    public class UpdateProfileDto
    {
        public string? FullName { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
    }

    public class ChangePasswordDto
    {
        public string OldPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public class UpdateFcmDto { public string Token { get; set; } = string.Empty; }
    public class DeleteAccountDto { public string Password { get; set; } = string.Empty; }
}