using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers
{
    // ═══════════════════════════════════════════════
    //  RATINGS CONTROLLER
    // ═══════════════════════════════════════════════
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RatingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        public RatingsController(ApplicationDbContext context) => _context = context;

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        // POST api/ratings  — العميل يقيم بعد التوصيل
        [HttpPost]
        public async Task<IActionResult> Rate([FromBody] CreateRatingDto dto)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .Include(o => o.Driver)
                .FirstOrDefaultAsync(o => o.Id == dto.OrderId &&
                                          o.CustomerId == userId &&
                                          o.Status == "Delivered");

            if (order == null)
                return BadRequest(new { message = "Order not found or not delivered yet" });

            if (await _context.Ratings.AnyAsync(r => r.OrderId == dto.OrderId))
                return BadRequest(new { message = "Order already rated" });

            var rating = new Rating
            {
                OrderId = dto.OrderId,
                CustomerId = userId,
                DriverId = order.DriverId,
                RestaurantId = order.RestaurantId,
                RestaurantRating = dto.RestaurantRating,
                DriverRating = dto.DriverRating,
                FoodRating = dto.FoodRating,
                Comment = dto.Comment,
                CreatedAt = DateTime.UtcNow
            };

            _context.Ratings.Add(rating);

            // تحديث متوسط تقييم المطعم
            var restaurant = await _context.Restaurants.FindAsync(order.RestaurantId);
            if (restaurant != null)
            {
                restaurant.Rating = ((restaurant.Rating * restaurant.TotalRatings) + dto.RestaurantRating)
                                    / (restaurant.TotalRatings + 1);
                restaurant.TotalRatings++;
            }

            // تحديث متوسط تقييم الطيار
            if (order.DriverId.HasValue && dto.DriverRating.HasValue)
            {
                var driver = await _context.Drivers.FindAsync(order.DriverId.Value);
                if (driver != null)
                {
                    driver.Rating = ((driver.Rating * driver.TotalRatings) + dto.DriverRating.Value)
                                    / (driver.TotalRatings + 1);
                    driver.TotalRatings++;
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = "Thank you for your rating!" });
        }

        // GET api/ratings/admin  — كل التقييمات (لوحة صاحب المنصة)
        [Authorize(Roles = "Admin")]
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllAdmin(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var total = await _context.Ratings.CountAsync();
            var ratings = await _context.Ratings
                .OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new
                {
                    r.Id,
                    CustomerName = r.Customer.FullName,
                    RestaurantName = r.Restaurant.Name,
                    DriverName = r.Driver != null ? r.Driver.User.FullName : null,
                    r.RestaurantRating,
                    r.FoodRating,
                    DriverRating = r.DriverRating.HasValue ? (double?)r.DriverRating.Value : null,
                    r.Comment,
                    r.CreatedAt
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = ratings });
        }

        // GET api/ratings/driver/{driverId}  — تقييمات الطيار
        [HttpGet("driver/{driverId}")]
        public async Task<IActionResult> GetDriverRatings(int driverId,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            var total = await _context.Ratings.CountAsync(r => r.DriverId == driverId);
            var ratings = await _context.Ratings
                .Where(r => r.DriverId == driverId)
                .OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new
                {
                    r.DriverRating,
                    r.Comment,
                    r.CreatedAt,
                    CustomerName = r.Customer.FullName
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = ratings });
        }
    }

    // ═══════════════════════════════════════════════
    //  NOTIFICATIONS CONTROLLER
    // ═══════════════════════════════════════════════
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubService _hubService;

        public NotificationsController(ApplicationDbContext context, IHubService hubService)
        {
            _context = context;
            _hubService = hubService;
        }

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        // GET api/notifications  — نوتيفيكيشنز المستخدم
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] bool? isRead,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var userId = GetUserId();

            var query = _context.Notifications
                .Where(n => n.UserId == userId)
                .AsQueryable();

            if (isRead.HasValue)
                query = query.Where(n => n.IsRead == isRead.Value);

            var total = await query.CountAsync();
            var unread = await _context.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

            var notifications = await query
                .OrderByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(n => new
                {
                    n.Id,
                    n.Title,
                    n.Body,
                    n.Type,
                    n.IsRead,
                    n.OrderId,
                    n.CreatedAt
                })
                .ToListAsync();

            return Ok(new { total, unread, page, pageSize, data = notifications });
        }

        // POST api/notifications/send  [Admin]
        [Authorize(Roles = "Admin")]
        [HttpPost("send")]
        public async Task<IActionResult> Send([FromBody] SendNotificationDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.Title) || string.IsNullOrWhiteSpace(dto.Body))
                return BadRequest(new { message = "Title and body are required" });

            List<User> targets;

            if (dto.UserId.HasValue)
            {
                var user = await _context.Users.FindAsync(dto.UserId.Value);
                if (user == null) return NotFound(new { message = "User not found" });
                targets = new List<User> { user };
            }
            else if (!string.IsNullOrWhiteSpace(dto.Role))
            {
                targets = await _context.Users
                    .Where(u => u.IsActive && u.Role == dto.Role)
                    .ToListAsync();
            }
            else
            {
                return BadRequest(new { message = "Provide UserId or Role" });
            }

            if (!targets.Any())
                return BadRequest(new { message = "No recipients found" });

            var type = string.IsNullOrWhiteSpace(dto.Type) ? "General" : dto.Type;
            var now = DateTime.UtcNow;
            var sent = 0;

            foreach (var user in targets)
            {
                _context.Notifications.Add(new Notification
                {
                    UserId = user.Id,
                    Title = dto.Title,
                    Body = dto.Body,
                    Type = type,
                    OrderId = dto.OrderId,
                    IsRead = false,
                    CreatedAt = now
                });

                await _hubService.NotifyUserDirectly(user.Id, "NotificationReceived", new
                {
                    dto.Title,
                    dto.Body,
                    Type = type,
                    dto.OrderId
                });
                sent++;
            }

            await _context.SaveChangesAsync();
            return Ok(new { message = $"Notification sent to {sent} user(s)", count = sent });
        }

        // PUT api/notifications/{id}/read  — تحديد نوتيفيكيشن كمقروء
        [HttpPut("{id}/read")]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var userId = GetUserId();
            var notif = await _context.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

            if (notif == null) return NotFound();

            notif.IsRead = true;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Marked as read" });
        }

        // PUT api/notifications/read-all  — تحديد الكل كمقروء
        [HttpPut("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = GetUserId();
            await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

            return Ok(new { message = "All notifications marked as read" });
        }

        // DELETE api/notifications/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = GetUserId();
            var notif = await _context.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

            if (notif == null) return NotFound();

            _context.Notifications.Remove(notif);
            await _context.SaveChangesAsync();
            return Ok(new { message = "Deleted" });
        }

        // DELETE api/notifications/clear-all
        [HttpDelete("clear-all")]
        public async Task<IActionResult> ClearAll()
        {
            var userId = GetUserId();
            await _context.Notifications
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync();

            return Ok(new { message = "All notifications cleared" });
        }
    }

    // DTOs
    public class CreateRatingDto
    {
        public int OrderId { get; set; }
        public int RestaurantRating { get; set; }
        public int? DriverRating { get; set; }
        public int? FoodRating { get; set; }
        public string? Comment { get; set; }
    }

    public class SendNotificationDto
    {
        public int? UserId { get; set; }
        public string? Role { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Type { get; set; }
        public int? OrderId { get; set; }
    }
}