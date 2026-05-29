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
    public class DriversController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public DriversController(ApplicationDbContext context) => _context = context;

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        // ─────────────────────────────────────────────
        // POST api/drivers/register  — تسجيل طيار جديد
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpPost("register")]
        public async Task<IActionResult> RegisterDriver([FromBody] RegisterDriverDto dto)
        {
            var userId = GetUserId();

            if (await _context.Drivers.AnyAsync(d => d.UserId == userId))
                return BadRequest(new { message = "Driver profile already exists" });

            var driver = new Driver
            {
                UserId = userId,
                VehicleType = dto.VehicleType,
                LicensePlate = dto.LicensePlate,
                NationalId = dto.NationalId,
                IsVerified = false,
                IsOnline = false,
                IsAvailable = true,
                JoinedAt = DateTime.UtcNow
            };

            _context.Drivers.Add(driver);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Driver registered, pending verification", driver.Id });
        }

        // ─────────────────────────────────────────────
        // GET api/drivers/me  — بروفايل الطيار
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpGet("me")]
        public async Task<IActionResult> GetMyProfile()
        {
            var userId = GetUserId();
            var driver = await _context.Drivers
                .Where(d => d.UserId == userId)
                .Select(d => new
                {
                    d.Id,
                    d.VehicleType,
                    d.LicensePlate,
                    d.Rating,
                    d.TotalRatings,
                    d.TotalDeliveries,
                    d.IsOnline,
                    d.IsAvailable,
                    d.IsVerified,
                    d.CurrentLatitude,
                    d.CurrentLongitude,
                    d.JoinedAt
                })
                .FirstOrDefaultAsync();

            if (driver == null) return NotFound(new { message = "Driver profile not found" });
            return Ok(driver);
        }

        // ─────────────────────────────────────────────
        // PUT api/drivers/toggle-online  — أونلاين / أوفلاين
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpPut("toggle-online")]
        public async Task<IActionResult> ToggleOnline()
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return NotFound();

            if (!driver.IsVerified)
                return BadRequest(new { message = "Your account is pending verification" });

            driver.IsOnline = !driver.IsOnline;
            await _context.SaveChangesAsync();
            return Ok(new { isOnline = driver.IsOnline, message = driver.IsOnline ? "You are now online" : "You are now offline" });
        }

        // ─────────────────────────────────────────────
        // PUT api/drivers/location  — تحديث الموقع (يُستدعى كل ثوان)
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpPut("location")]
        public async Task<IActionResult> UpdateLocation([FromBody] UpdateLocationDto dto)
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return NotFound();

            // تحديث الموقع الحالي في جدول Drivers
            driver.CurrentLatitude = dto.Latitude;
            driver.CurrentLongitude = dto.Longitude;
            driver.LastLocationUpdate = DateTime.UtcNow;

            // تسجيل في DriverLocations لو الطيار بيوصل طلب
            if (dto.OrderId.HasValue)
            {
                _context.DriverLocations.Add(new DriverLocation
                {
                    DriverId = driver.Id,
                    OrderId = dto.OrderId,
                    Latitude = dto.Latitude,
                    Longitude = dto.Longitude,
                    Speed = dto.Speed,
                    Heading = dto.Heading,
                    Timestamp = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Location updated" });
        }

        // ─────────────────────────────────────────────
        // GET api/drivers/{orderId}/location  — العميل يتابع الطيار
        // ─────────────────────────────────────────────
        [HttpGet("{orderId}/location")]
        public async Task<IActionResult> GetDriverLocation(int orderId)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == userId);
            if (order == null) return NotFound(new { message = "Order not found" });

            if (order.DriverId == null)
                return BadRequest(new { message = "No driver assigned yet" });

            var driver = await _context.Drivers
                .Where(d => d.Id == order.DriverId)
                .Select(d => new
                {
                    d.CurrentLatitude,
                    d.CurrentLongitude,
                    d.LastLocationUpdate,
                    d.IsOnline,
                    DriverName = d.User.FullName,
                    DriverPhone = d.User.Phone,
                    d.Rating,
                    d.VehicleType
                })
                .FirstOrDefaultAsync();

            return Ok(driver);
        }

        // ─────────────────────────────────────────────
        // GET api/drivers/earnings  — إيرادات الطيار
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpGet("earnings")]
        public async Task<IActionResult> GetEarnings([FromQuery] string period = "today")
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return NotFound();

            DateTime from = period switch
            {
                "today" => DateTime.UtcNow.Date,
                "week" => DateTime.UtcNow.Date.AddDays(-7),
                "month" => DateTime.UtcNow.Date.AddDays(-30),
                _ => DateTime.UtcNow.Date
            };

            var deliveries = await _context.Orders
                .Where(o => o.DriverId == driver.Id &&
                            o.Status == "Delivered" &&
                            o.DeliveredAt >= from)
                .Select(o => new
                {
                    o.Id,
                    o.TotalAmount,
                    o.DeliveryFee,
                    o.DeliveredAt,
                    RestaurantName = o.Restaurant.Name
                })
                .OrderByDescending(o => o.DeliveredAt)
                .ToListAsync();

            return Ok(new
            {
                period,
                totalDeliveries = deliveries.Count,
                totalEarnings = deliveries.Sum(d => d.DeliveryFee),
                deliveries
            });
        }

        // ─────────────────────────────────────────────
        // GET api/drivers/orders/active  — الطلب الحالي للطيار
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpGet("orders/active")]
        public async Task<IActionResult> GetActiveOrder()
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return NotFound();

            var order = await _context.Orders
                .Where(o => o.DriverId == driver.Id &&
                            new[] { "OnTheWay", "ReadyForPickup" }.Contains(o.Status))
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.TotalAmount,
                    o.DeliveryFee,
                    o.DeliveryAddress,
                    o.DeliveryLatitude,
                    o.DeliveryLongitude,
                    o.DeliveryNotes,
                    CustomerName = o.Customer.FullName,
                    // CustomerPhone = o.Customer.Phone, // Hidden as per requirement
                    RestaurantName = o.Restaurant.Name,
                    RestaurantLat = o.Restaurant.Latitude,
                    RestaurantLng = o.Restaurant.Longitude,
                    Items = o.OrderItems.Select(i => new
                    {
                        ProductName = i.Product.Name,
                        i.Quantity,
                        i.Notes
                    })
                })
                .FirstOrDefaultAsync();

            if (order == null) return Ok(new { message = "No active order" });
            return Ok(order);
        }

        // ─────────────────────────────────────────────
        // GET api/drivers/admin  — كل الطيارين (لوحة صاحب المنصة)
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllAdmin(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            var total = await _context.Drivers.CountAsync();
            var drivers = await _context.Drivers
                .OrderByDescending(d => d.JoinedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new
                {
                    d.Id,
                    d.UserId,
                    UserName = d.User.FullName,
                    FullName = d.User.FullName,
                    Email = d.User.Email,
                    d.VehicleType,
                    d.LicensePlate,
                    d.NationalId,
                    d.Rating,
                    d.TotalRatings,
                    d.TotalDeliveries,
                    d.IsOnline,
                    d.IsAvailable,
                    d.IsVerified,
                    d.CurrentLatitude,
                    d.CurrentLongitude,
                    d.JoinedAt
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = drivers });
        }

        // ─────────────────────────────────────────────
        // PUT api/drivers/{id}/verify  [Admin]
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpPut("{id}/verify")]
        public async Task<IActionResult> VerifyDriver(int id)
        {
            var driver = await _context.Drivers.FindAsync(id);
            if (driver == null) return NotFound();

            driver.IsVerified = true;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Driver verified successfully" });
        }
    }

    // DTOs
    public class RegisterDriverDto
    {
        public string VehicleType { get; set; } = string.Empty;
        public string LicensePlate { get; set; } = string.Empty;
        public string? NationalId { get; set; }
    }

    public class UpdateLocationDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? Speed { get; set; }
        public double? Heading { get; set; }
        public int? OrderId { get; set; }
    }
}