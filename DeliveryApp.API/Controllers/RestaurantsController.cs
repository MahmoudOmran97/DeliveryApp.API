using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RestaurantsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RestaurantsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────
        // GET api/restaurants
        // عرض كل المطاعم مع فلترة + pagination
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? search,
            [FromQuery] bool? isOpen,
            [FromQuery] string? sortBy,   // "rating" | "deliveryFee" | "estimatedTime"
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            var query = _context.Restaurants
                .Where(r => r.IsActive)
                .AsQueryable();

            // فلتر البحث بالاسم أو العنوان
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(r =>
                    r.Name.Contains(search) ||
                    r.Address.Contains(search));

            // فلتر الحالة (مفتوح / مغلق)
            if (isOpen.HasValue)
                query = query.Where(r => r.IsOpen == isOpen.Value);

            // الترتيب
            query = sortBy switch
            {
                "rating" => query.OrderByDescending(r => r.Rating),
                "deliveryFee" => query.OrderBy(r => r.DeliveryFee),
                "estimatedTime" => query.OrderBy(r => r.EstimatedTime),
                _ => query.OrderByDescending(r => r.Rating)
            };

            var total = await query.CountAsync();

            var restaurants = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.Description,
                    r.Address,
                    r.ImageUrl,
                    r.CoverImageUrl,
                    r.Rating,
                    r.TotalRatings,
                    r.DeliveryFee,
                    r.MinOrderAmount,
                    r.EstimatedTime,
                    r.IsOpen
                })
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(total / (double)pageSize),
                data = restaurants
            });
        }

        // ─────────────────────────────────────────────
        // GET api/restaurants/{id}
        // تفاصيل مطعم معين
        // ─────────────────────────────────────────────
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var restaurant = await _context.Restaurants
                .Where(r => r.Id == id && r.IsActive)
                .Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.Description,
                    r.Address,
                    r.Latitude,
                    r.Longitude,
                    r.ImageUrl,
                    r.CoverImageUrl,
                    r.Phone,
                    r.Rating,
                    r.TotalRatings,
                    r.DeliveryFee,
                    r.MinOrderAmount,
                    r.EstimatedTime,
                    r.IsOpen
                })
                .FirstOrDefaultAsync();

            if (restaurant == null)
                return NotFound(new { message = "Restaurant not found" });

            return Ok(restaurant);
        }

        // ─────────────────────────────────────────────
        // GET api/restaurants/{id}/menu
        // قائمة الأكل: أقسام + منتجات
        // ─────────────────────────────────────────────
        [HttpGet("{id}/menu")]
        public async Task<IActionResult> GetMenu(int id)
        {
            var exists = await _context.Restaurants
                .AnyAsync(r => r.Id == id && r.IsActive);

            if (!exists)
                return NotFound(new { message = "Restaurant not found" });

            var menu = await _context.Categories
                .Where(c => c.RestaurantId == id && c.IsActive)
                .OrderBy(c => c.SortOrder)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.ImageUrl,
                    Products = c.Products
                        .Where(p => p.IsActive && p.IsAvailable)
                        .Select(p => new
                        {
                            p.Id,
                            p.Name,
                            p.Description,
                            p.Price,
                            p.DiscountedPrice,
                            p.ImageUrl,
                            p.PreparationTime,
                            p.Calories,
                            p.IsAvailable
                        })
                        .ToList()
                })
                .ToListAsync();

            return Ok(menu);
        }

        // ─────────────────────────────────────────────
        // GET api/restaurants/nearby
        // مطاعم قريبة من موقع العميل
        // ─────────────────────────────────────────────
        [HttpGet("nearby")]
        public async Task<IActionResult> GetNearby(
            [FromQuery] double lat,
            [FromQuery] double lng,
            [FromQuery] double radiusKm = 10)
        {
            // حساب المسافة بـ Haversine Formula
            var restaurants = await _context.Restaurants
                .Where(r => r.IsActive && r.IsOpen)
                .ToListAsync();

            var nearby = restaurants
                .Select(r => new
                {
                    r.Id,
                    r.Name,
                    r.ImageUrl,
                    r.Rating,
                    r.DeliveryFee,
                    r.EstimatedTime,
                    r.IsOpen,
                    DistanceKm = GetDistance(lat, lng, r.Latitude, r.Longitude)
                })
                .Where(r => r.DistanceKm <= radiusKm)
                .OrderBy(r => r.DistanceKm)
                .ToList();

            return Ok(nearby);
        }

        // ─────────────────────────────────────────────
        // GET api/restaurants/{id}/reviews
        // تقييمات المطعم
        // ─────────────────────────────────────────────
        [HttpGet("{id}/reviews")]
        public async Task<IActionResult> GetReviews(
            int id,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            var exists = await _context.Restaurants
                .AnyAsync(r => r.Id == id && r.IsActive);

            if (!exists)
                return NotFound(new { message = "Restaurant not found" });

            var total = await _context.Ratings
                .CountAsync(r => r.RestaurantId == id);

            var reviews = await _context.Ratings
                .Where(r => r.RestaurantId == id)
                .OrderByDescending(r => r.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => new
                {
                    r.Id,
                    CustomerName = r.Customer.FullName,
                    CustomerImage = r.Customer.ProfileImageUrl,
                    r.RestaurantRating,
                    r.FoodRating,
                    r.Comment,
                    r.CreatedAt
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = reviews });
        }

        // ─────────────────────────────────────────────
        // POST api/restaurants  [Admin Only]
        // إضافة مطعم جديد
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRestaurantDto dto)
        {
            var restaurant = new Restaurant
            {
                Name = dto.Name,
                Description = dto.Description,
                Address = dto.Address,
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                Phone = dto.Phone,
                DeliveryFee = dto.DeliveryFee,
                MinOrderAmount = dto.MinOrderAmount,
                EstimatedTime = dto.EstimatedTime,
                IsOpen = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            _context.Restaurants.Add(restaurant);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById),
                new { id = restaurant.Id },
                new { restaurant.Id, restaurant.Name });
        }

        // ─────────────────────────────────────────────
        // PUT api/restaurants/{id}/toggle-status  [Admin]
        // فتح / غلق المطعم
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpPut("{id}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var restaurant = await _context.Restaurants.FindAsync(id);

            if (restaurant == null)
                return NotFound(new { message = "Restaurant not found" });

            restaurant.IsOpen = !restaurant.IsOpen;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = restaurant.IsOpen ? "Restaurant is now open" : "Restaurant is now closed",
                isOpen = restaurant.IsOpen
            });
        }

        // ─────────────────────────────────────────────
        // Helper – Haversine Distance (km)
        // ─────────────────────────────────────────────
        private static double GetDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371;
            var dLat = ToRad(lat2 - lat1);
            var dLon = ToRad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                  + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double ToRad(double deg) => deg * (Math.PI / 180);
    }

    // ─────────────────────────────────────────────
    // DTOs
    // ─────────────────────────────────────────────
    public class CreateRestaurantDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Address { get; set; } = string.Empty;
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? Phone { get; set; }
        public decimal DeliveryFee { get; set; }
        public decimal MinOrderAmount { get; set; }
        public int EstimatedTime { get; set; } = 30;
    }
}