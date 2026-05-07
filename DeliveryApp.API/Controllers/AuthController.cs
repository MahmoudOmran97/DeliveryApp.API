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

        public AuthController(
            ApplicationDbContext context,
            IJwtService jwtService)
        {
            _context = context;
            _jwtService = jwtService;
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
    }
}