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

        // GET api/user/all  — كل المستخدمين (لوحة صاحب المنصة)
        [Authorize(Roles = "Admin")]
        [HttpGet("all")]
        public async Task<IActionResult> GetAllUsers(
            [FromQuery] string? role,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(role))
                query = query.Where(u => u.Role == role);

            var total = await query.CountAsync();
            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    u.Id,
                    u.FullName,
                    u.Email,
                    u.Phone,
                    u.Role,
                    u.Address,
                    u.ProfileImageUrl,
                    u.IsActive,
                    u.CreatedAt,
                    RestaurantId = _context.Restaurants.Where(r => r.OwnerUserId == u.Id).Select(r => (int?)r.Id).FirstOrDefault(),
                    RestaurantName = _context.Restaurants.Where(r => r.OwnerUserId == u.Id).Select(r => r.Name).FirstOrDefault()
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = users });
        }

        // GET api/user/{id}  [Admin]
        [Authorize(Roles = "Admin")]
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetUserById(int id)
        {
            var user = await _context.Users
                .Where(u => u.Id == id)
                .Select(u => new
                {
                    u.Id,
                    u.FullName,
                    u.Email,
                    u.Phone,
                    u.Role,
                    u.Address,
                    u.ProfileImageUrl,
                    u.IsActive,
                    u.CreatedAt,
                    RestaurantId = _context.Restaurants.Where(r => r.OwnerUserId == u.Id).Select(r => (int?)r.Id).FirstOrDefault(),
                    RestaurantName = _context.Restaurants.Where(r => r.OwnerUserId == u.Id).Select(r => r.Name).FirstOrDefault()
                })
                .FirstOrDefaultAsync();

            if (user == null) return NotFound();
            return Ok(user);
        }

        // POST api/user/admin  — إنشاء مستخدم (Admin)
        [Authorize(Roles = "Admin")]
        [HttpPost("admin")]
        public async Task<IActionResult> CreateUser([FromBody] AdminCreateUserDto dto)
        {
            // ── Validation ────────────────────────────────────────────────
            if (dto == null)
                return BadRequest(new { message = "Request body is required" });

            if (string.IsNullOrWhiteSpace(dto.FullName))
                return BadRequest(new { message = "Full name is required" });

            if (string.IsNullOrWhiteSpace(dto.Email))
                return BadRequest(new { message = "Email is required" });

            if (string.IsNullOrWhiteSpace(dto.Password))
                return BadRequest(new { message = "Password is required" });

            if (dto.Password.Length < 6)
                return BadRequest(new { message = "Password must be at least 6 characters" });

            if (string.IsNullOrWhiteSpace(dto.Phone))
                return BadRequest(new { message = "Phone is required" });

            var allowedRoles = new[] { "Admin", "Restaurant", "Driver", "Customer" };
            if (string.IsNullOrWhiteSpace(dto.Role) || !allowedRoles.Contains(dto.Role))
                return BadRequest(new { message = $"Invalid role. Allowed: {string.Join(", ", allowedRoles)}" });

            if (await _context.Users.AnyAsync(u => u.Email == dto.Email))
                return BadRequest(new { message = "Email already exists" });

            // ── Create user ───────────────────────────────────────────────
            try
            {
                var user = new User
                {
                    FullName = dto.FullName.Trim(),
                    Email = dto.Email.Trim().ToLower(),
                    Phone = dto.Phone.Trim(),
                    Role = dto.Role,
                    Address = dto.Address?.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                // Assign restaurant owner if role is Restaurant
                if (dto.Role == "Restaurant" && dto.RestaurantId.HasValue)
                {
                    var restaurant = await _context.Restaurants.FindAsync(dto.RestaurantId.Value);
                    if (restaurant == null)
                        return BadRequest(new { message = "Restaurant not found" });

                    restaurant.OwnerUserId = user.Id;
                    await _context.SaveChangesAsync();
                }

                return Ok(new { message = "User created successfully", user.Id, user.FullName, user.Email, user.Role });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                if (inner.Contains("UNIQUE") || inner.Contains("duplicate") || inner.Contains("unique"))
                    return BadRequest(new { message = "Email already exists" });
                return StatusCode(500, new { message = $"Database error: {inner}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Unexpected error: {ex.InnerException?.Message ?? ex.Message}" });
            }
        }

        // PUT api/user/{id}  [Admin]
        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateUser(int id, [FromBody] AdminUpdateUserDto dto)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { message = "User not found" });

            try
            {
                if (!string.IsNullOrWhiteSpace(dto.FullName)) user.FullName = dto.FullName.Trim();
                if (!string.IsNullOrWhiteSpace(dto.Phone)) user.Phone = dto.Phone.Trim();
                if (dto.Address != null) user.Address = dto.Address.Trim();
                if (!string.IsNullOrWhiteSpace(dto.Role))
                {
                    var allowedRoles = new[] { "Admin", "Restaurant", "Driver", "Customer" };
                    if (!allowedRoles.Contains(dto.Role))
                        return BadRequest(new { message = $"Invalid role. Allowed: {string.Join(", ", allowedRoles)}" });
                    user.Role = dto.Role;
                }
                if (dto.IsActive.HasValue) user.IsActive = dto.IsActive.Value;
                if (!string.IsNullOrWhiteSpace(dto.Password))
                    user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);

                user.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                if (dto.RestaurantId.HasValue && user.Role == "Restaurant")
                {
                    var oldRestaurants = await _context.Restaurants.Where(r => r.OwnerUserId == id).ToListAsync();
                    foreach (var r in oldRestaurants) r.OwnerUserId = null;

                    var restaurant = await _context.Restaurants.FindAsync(dto.RestaurantId.Value);
                    if (restaurant != null)
                    {
                        restaurant.OwnerUserId = id;
                        await _context.SaveChangesAsync();
                    }
                }

                return Ok(new { message = "User updated successfully" });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                var inner = dbEx.InnerException?.Message ?? dbEx.Message;
                return StatusCode(500, new { message = $"Database error: {inner}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Unexpected error: {ex.InnerException?.Message ?? ex.Message}" });
            }
        }

        // PUT api/user/{id}/toggle-active  [Admin]
        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}/toggle-active")]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.IsActive = !user.IsActive;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = user.IsActive ? "User activated" : "User deactivated", user.IsActive });
        }

        // PUT api/user/{id}/assign-restaurant/{restaurantId}  [Admin]
        [Authorize(Roles = "Admin")]
        [HttpPut("{id:int}/assign-restaurant/{restaurantId:int}")]
        public async Task<IActionResult> AssignRestaurant(int id, int restaurantId)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { message = "User not found" });
            if (user.Role != "Restaurant")
                return BadRequest(new { message = "User must have Restaurant role" });

            var restaurant = await _context.Restaurants.FindAsync(restaurantId);
            if (restaurant == null) return NotFound(new { message = "Restaurant not found" });

            var previous = await _context.Restaurants.Where(r => r.OwnerUserId == id && r.Id != restaurantId).ToListAsync();
            foreach (var r in previous) r.OwnerUserId = null;

            restaurant.OwnerUserId = id;
            user.Role = "Restaurant";
            await _context.SaveChangesAsync();

            return Ok(new { message = "Restaurant assigned to owner", restaurantId, userId = id });
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

    public class AdminCreateUserDto
    {
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = "Customer";
        public string? Address { get; set; }
        public int? RestaurantId { get; set; }
    }

    public class AdminUpdateUserDto
    {
        public string? FullName { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? Role { get; set; }
        public string? Password { get; set; }
        public bool? IsActive { get; set; }
        public int? RestaurantId { get; set; }
    }
}