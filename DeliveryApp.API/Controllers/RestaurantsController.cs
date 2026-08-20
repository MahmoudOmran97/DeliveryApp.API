// ملف معدّل: DeliveryApp.API/Controllers/RestaurantsController.cs
// التغييرات فقط على الـ endpoints الخاصة بتطبيق المطعم:
//   - desktop-update  : كان AllowAnonymous → صار [Authorize(Roles="Restaurant,Admin")] + تحقق Ownership
//   - toggle-status   : كان AllowAnonymous → صار [Authorize(Roles="Restaurant,Admin")] + تحقق Ownership
// باقي الـ endpoints (GET public) زي ما هي

using DeliveryApp.API.Authorization;
using DeliveryApp.API.DTOs.Revenue;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RestaurantsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    public RestaurantsController(ApplicationDbContext context) => _context = context;

    // ─── GET /api/restaurants  (public) ─────────────────────────────────────
    // Supports: search, isOpen, sortBy, page, pageSize
    //           lat / lng / radiusKm   → location-based filtering
    //           category               → StoreType filter (Restaurant, Pharmacy, ...)
    //           minRating              → minimum rating filter
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? search,
        [FromQuery] bool? isOpen,
        [FromQuery] string? sortBy,
        [FromQuery] double? lat,
        [FromQuery] double? lng,
        [FromQuery] double radiusKm = 10.0,
        [FromQuery] string? category = null,
        [FromQuery] double minRating = 0.0,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        var query = _context.Restaurants.Where(r => r.IsActive).AsQueryable();

        // ── text search ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => r.Name.Contains(search) || r.Address.Contains(search));

        // ── open/closed filter ───────────────────────────────────────────────
        if (isOpen.HasValue)
            query = query.Where(r => r.IsOpen == isOpen.Value);

        // ── StoreType / category filter ──────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(r => r.StoreType == category);

        // ── min rating filter ────────────────────────────────────────────────
        if (minRating > 0)
            query = query.Where(r => r.Rating >= minRating);

        // ── sort ─────────────────────────────────────────────────────────────
        query = sortBy switch
        {
            "rating" => query.OrderByDescending(r => r.Rating),
            "deliveryFee" => query.OrderBy(r => r.DeliveryFee),
            "estimatedTime" => query.OrderBy(r => r.EstimatedTime),
            _ => query.OrderByDescending(r => r.Rating)
        };

        // ── fetch all that pass the DB filters ───────────────────────────────
        var dbList = await query
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
                r.Rating,
                r.TotalRatings,
                r.DeliveryFee,
                r.MinOrderAmount,
                r.EstimatedTime,
                r.IsOpen,
                r.StoreType,
                r.OwnerUserId,
                OwnerName = r.Owner != null ? r.Owner.FullName : null
            })
            .ToListAsync();

        // ── in-memory location filter (Haversine) ────────────────────────────
        bool useLocation = lat.HasValue && lng.HasValue;

        // نجيب إعدادات التوصيل مرة واحدة بس قبل اللوب (قابلة للتعديل من الأدمن)
        var (freeRadiusKm, extraFeePerKm) = useLocation
            ? await DeliveryFeeCalculator.GetSettingsAsync(_context)
            : (DeliveryFeeCalculator.DefaultFreeRadiusKm, DeliveryFeeCalculator.DefaultExtraFeePerKm);

        var projected = dbList
            .Select(r =>
            {
                double? distKm = useLocation
                    ? DeliveryFeeCalculator.GetDistanceKm(lat!.Value, lng!.Value, r.Latitude, r.Longitude)
                    : (double?)null;

                // لو معانا موقع العميل، نحسب سعر التوصيل الفعلي (أساسي + زيادة المسافة)
                var effectiveDeliveryFee = distKm.HasValue
                    ? DeliveryFeeCalculator.Calculate(r.DeliveryFee, distKm.Value, freeRadiusKm, extraFeePerKm)
                    : r.DeliveryFee;

                return new
                {
                    r.Id,
                    r.Name,
                    r.Description,
                    r.Address,
                    r.ImageUrl,
                    r.CoverImageUrl,
                    r.Rating,
                    r.TotalRatings,
                    DeliveryFee = effectiveDeliveryFee,
                    r.MinOrderAmount,
                    r.EstimatedTime,
                    r.IsOpen,
                    r.StoreType,
                    r.OwnerUserId,
                    r.OwnerName,
                    DistanceKm = distKm
                };
            })
            .Where(r => !useLocation || r.DistanceKm!.Value <= radiusKm)
            .OrderBy(r => useLocation ? r.DistanceKm : null)   // nearest first when location given
            .ThenByDescending(r => r.Rating)
            .ToList();

        var total = projected.Count;
        var paged = projected.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(new
        {
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize),
            data = paged
        });
    }

    // ─── GET /api/restaurants/me  (صاحب المحل بحسابه، من غير ما يعرف الـ Id) ──
    [Authorize(Roles = "Restaurant,Admin")]
    [HttpGet("me")]
    public async Task<IActionResult> GetMine()
    {
        var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
        if (restaurantId == null) return NotFound(new { message = "مفيش محل مرتبط بالحساب ده" });

        var restaurant = await _context.Restaurants
            .Where(r => r.Id == restaurantId.Value)
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
                DeliveryFee = r.DeliveryFee,
                r.MinOrderAmount,
                r.EstimatedTime,
                r.IsOpen,
                r.IsActive,
                r.StoreType,
                r.OwnerUserId,
                OwnerName = r.Owner != null ? r.Owner.FullName : null,
                OwnerEmail = r.Owner != null ? r.Owner.Email : null,
                r.CreatedAt
            })
            .FirstOrDefaultAsync();

        if (restaurant == null) return NotFound(new { message = "Restaurant not found" });
        return Ok(restaurant);
    }

    // ─────────────────────────────────────────────
    // GET /api/restaurants/my-dues  — مستحقات صاحب المحل للمنصة (اشتراك/عمولة)
    // بيرجع كل السجلات (الأحدث الأول)، عرض بس - الأدمن هو الوحيد اللي يقدر
    // يغيّر الـ Status (عن طريق /api/revenue/settlements/{id}/mark-paid)
    // نفس فكرة GET /api/drivers/my-dues بالظبط بس للمحل بدل السواق.
    // ─────────────────────────────────────────────
    [Authorize(Roles = "Restaurant")]
    [HttpGet("my-dues")]
    public async Task<IActionResult> GetMyDues()
    {
        var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
        if (restaurantId == null) return NotFound(new { message = "مفيش محل مرتبط بالحساب ده" });

        var dues = await _context.RevenueSettlements
            .Where(s => s.EntityType == RevenueEntityType.Store && s.RestaurantId == restaurantId.Value)
            .OrderByDescending(s => s.PeriodStart)
            .Select(s => new StoreDueDto
            {
                Id = s.Id,
                PeriodStart = s.PeriodStart,
                PeriodEnd = s.PeriodEnd,
                OrdersCount = s.OrdersCount,
                OrdersTotal = s.OrdersTotal,
                AmountDue = s.AmountDue,
                AmountPaid = s.AmountPaid,
                Status = s.Status.ToString(),
                PaidAt = s.PaidAt,
                Notes = s.Notes
            })
            .ToListAsync();

        return Ok(dues);
    }

    // ─────────────────────────────────────────────
    // GET /api/restaurants/my-dues/summary  — ملخص سريع (اختصار في الداشبورد)
    // ─────────────────────────────────────────────
    [Authorize(Roles = "Restaurant")]
    [HttpGet("my-dues/summary")]
    public async Task<IActionResult> GetMyDuesSummary()
    {
        var restaurantId = await RestaurantOwnerAuth.GetOwnerRestaurantIdAsync(User, _context);
        if (restaurantId == null) return NotFound(new { message = "مفيش محل مرتبط بالحساب ده" });

        var dues = await _context.RevenueSettlements
            .Where(s => s.EntityType == RevenueEntityType.Store && s.RestaurantId == restaurantId.Value)
            .OrderByDescending(s => s.PeriodStart)
            .ToListAsync();

        var pending = dues.Where(s => s.Status != SettlementStatus.Paid).ToList();
        var latest = dues.FirstOrDefault();

        var summary = new StoreDuesSummaryDto
        {
            HasPending = pending.Any(),
            PendingAmount = pending.Sum(s => s.AmountDue - s.AmountPaid),
            PendingCount = pending.Count,
            LatestDue = latest == null ? null : new StoreDueDto
            {
                Id = latest.Id,
                PeriodStart = latest.PeriodStart,
                PeriodEnd = latest.PeriodEnd,
                OrdersCount = latest.OrdersCount,
                OrdersTotal = latest.OrdersTotal,
                AmountDue = latest.AmountDue,
                AmountPaid = latest.AmountPaid,
                Status = latest.Status.ToString(),
                PaidAt = latest.PaidAt,
                Notes = latest.Notes
            }
        };

        return Ok(summary);
    }

    // ─── GET /api/restaurants/admin/map  [Admin]  — كل المحلات بالإحداثيات + عدد الطلبات النشطة
    //     (لخريطة الأدمن الحية: السواقين + المحلات في نفس الشاشة)
    // ملحوظة: لازم تتحط قبل [HttpGet("{id}")] عشان الراوتنج يفرّق "admin" عن {id}
    //          (أصلاً ASP.NET Core بيفضّل الـ literal route تلقائي، بس تركناها هنا كمان كتوضيح)
    [Authorize(Roles = "Admin")]
    [HttpGet("admin/map")]
    public async Task<IActionResult> GetForAdminMap()
    {
        var activeStatuses = new[] { "Pending", "Accepted", "Preparing", "ReadyForPickup" };

        var restaurants = await _context.Restaurants
            .Where(r => r.IsActive)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.StoreType,
                r.Address,
                r.ImageUrl,
                r.Latitude,
                r.Longitude,
                r.IsOpen,
                PendingOrders = r.Orders.Count(o => activeStatuses.Contains(o.Status))
            })
            .ToListAsync();

        return Ok(restaurants);
    }

    // ─── GET /api/restaurants/{id}  (public) ────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, [FromQuery] double? lat, [FromQuery] double? lng)
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
                BaseDeliveryFee = r.DeliveryFee,
                r.MinOrderAmount,
                r.EstimatedTime,
                r.IsOpen,
                r.IsActive,
                r.StoreType,
                r.OwnerUserId,
                OwnerName = r.Owner != null ? r.Owner.FullName : null,
                OwnerEmail = r.Owner != null ? r.Owner.Email : null
            })
            .FirstOrDefaultAsync();

        if (restaurant == null)
            return NotFound(new { message = "Restaurant not found" });

        double? distanceKm = (lat.HasValue && lng.HasValue)
            ? DeliveryFeeCalculator.GetDistanceKm(lat.Value, lng.Value, restaurant.Latitude, restaurant.Longitude)
            : (double?)null;

        decimal effectiveDeliveryFee = restaurant.BaseDeliveryFee;
        if (distanceKm.HasValue)
        {
            var (freeRadiusKm, extraFeePerKm) = await DeliveryFeeCalculator.GetSettingsAsync(_context);
            effectiveDeliveryFee = DeliveryFeeCalculator.Calculate(restaurant.BaseDeliveryFee, distanceKm.Value, freeRadiusKm, extraFeePerKm);
        }

        return Ok(new
        {
            restaurant.Id,
            restaurant.Name,
            restaurant.Description,
            restaurant.Address,
            restaurant.Latitude,
            restaurant.Longitude,
            restaurant.ImageUrl,
            restaurant.CoverImageUrl,
            restaurant.Phone,
            restaurant.Rating,
            restaurant.TotalRatings,
            DeliveryFee = effectiveDeliveryFee,
            restaurant.MinOrderAmount,
            restaurant.EstimatedTime,
            restaurant.IsOpen,
            restaurant.IsActive,
            restaurant.StoreType,
            restaurant.OwnerUserId,
            restaurant.OwnerName,
            restaurant.OwnerEmail,
            DistanceKm = distanceKm
        });
    }

    // ─── GET /api/restaurants/{id}/menu  (public) ───────────────────────────
    // ✅ FEATURE: كل منتج بيرجع دلوقتي SalesCount (عدد مرات بيعه فعليًا في أوردرات
    // "Delivered") و IsBestSeller (true لو من أعلى 10 منتجات مبيعًا في المحل ده،
    // بشرط يبقى اتباع مرة واحدة على الأقل) — العميل (Customer app) بيستخدمهم في
    // فرز/فلترة صفحة "الأفضل مبيعًا".
    [HttpGet("{id}/menu")]
    public async Task<IActionResult> GetMenu(int id)
    {
        var exists = await _context.Restaurants.AnyAsync(r => r.Id == id && r.IsActive);
        if (!exists) return NotFound(new { message = "Restaurant not found" });

        // عدد مرات بيع كل منتج (Quantity الإجمالية في أوردرات اتسلمت فعلاً)
        var salesCounts = await _context.OrderItems
            .Where(oi => oi.Order.RestaurantId == id && oi.Order.Status == "Delivered")
            .GroupBy(oi => oi.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Count);

        // أعلى 10 منتجات مبيعًا في المحل (SalesCount > 0)
        var bestSellerIds = salesCounts
            .OrderByDescending(x => x.Value)
            .Take(10)
            .Select(x => x.Key)
            .ToHashSet();

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
                        p.IsAvailable,
                        CategoryId = c.Id,
                        CategoryName = c.Name,
                        Variants = p.Variants
                            .Where(v => v.IsActive)
                            .OrderBy(v => v.SortOrder)
                            .Select(v => new { v.Id, v.Name, v.Price, v.SortOrder })
                            .ToList()
                    }).ToList()
            })
            .ToListAsync();

        // بنلحق SalesCount/IsBestSeller بعد الاستعلام (Dictionary lookup مش قابل للترجمة
        // في نفس الـ EF query فوق) — anonymous types بنعمل منها object جديد فيه الحقلين.
        var result = menu.Select(c => new
        {
            c.Id,
            c.Name,
            c.ImageUrl,
            Products = c.Products.Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.Price,
                p.DiscountedPrice,
                p.ImageUrl,
                p.PreparationTime,
                p.Calories,
                p.IsAvailable,
                p.CategoryId,
                p.CategoryName,
                p.Variants,
                SalesCount = salesCounts.TryGetValue(p.Id, out var cnt) ? cnt : 0,
                IsBestSeller = bestSellerIds.Contains(p.Id)
            }).ToList()
        }).ToList();

        return Ok(result);
    }

    // ─── GET /api/restaurants/{id}/products/{productId}/related  (public) ──
    // ✅ FEATURE: "بيتطلب مع" — منتجات اتشترت فعليًا مع المنتج ده في نفس الأوردر
    // (تحليل حقيقي من جدول OrderItems)، مرتبة حسب عدد مرات الاشتراك. لو مفيش
    // بيانات كفاية (منتج جديد/محل جديد)، بنرجع بدالها أعلى منتجات مبيعًا من
    // نفس الكاتيجوري كـ fallback بديهي.
    [HttpGet("{id}/products/{productId}/related")]
    public async Task<IActionResult> GetRelatedProducts(int id, int productId, [FromQuery] int take = 6)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == productId && p.Category.RestaurantId == id);
        if (product == null) return NotFound(new { message = "Product not found" });

        // الأوردرات اللي اشترت المنتج ده
        var orderIds = await _context.OrderItems
            .Where(oi => oi.ProductId == productId)
            .Select(oi => oi.OrderId)
            .Distinct()
            .ToListAsync();

        var coProductIds = new List<int>();
        if (orderIds.Count > 0)
        {
            coProductIds = await _context.OrderItems
                .Where(oi => orderIds.Contains(oi.OrderId) && oi.ProductId != productId)
                .GroupBy(oi => oi.ProductId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(take)
                .ToListAsync();
        }

        // لو مفيش نتيجة كفاية، كمّل بمنتجات تانية من نفس الكاتيجوري (الأعلى مبيعًا)
        if (coProductIds.Count < take)
        {
            var salesCounts = await _context.OrderItems
                .Where(oi => oi.Order.RestaurantId == id && oi.Order.Status == "Delivered")
                .GroupBy(oi => oi.ProductId)
                .Select(g => new { ProductId = g.Key, Count = g.Sum(x => x.Quantity) })
                .ToDictionaryAsync(x => x.ProductId, x => x.Count);

            var fallbackIds = await _context.Products
                .Where(p => p.CategoryId == product.CategoryId && p.IsActive && p.IsAvailable
                    && p.Id != productId && !coProductIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync();

            var ordered = fallbackIds
                .OrderByDescending(pid => salesCounts.TryGetValue(pid, out var c) ? c : 0)
                .Take(take - coProductIds.Count);

            coProductIds.AddRange(ordered);
        }

        if (coProductIds.Count == 0) return Ok(new List<object>());

        var products = await _context.Products
            .Where(p => coProductIds.Contains(p.Id) && p.IsActive && p.IsAvailable)
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
                p.IsAvailable,
                CategoryId = p.CategoryId,
                CategoryName = p.Category.Name,
                Variants = p.Variants
                    .Where(v => v.IsActive)
                    .OrderBy(v => v.SortOrder)
                    .Select(v => new { v.Id, v.Name, v.Price, v.SortOrder })
                    .ToList()
            })
            .ToListAsync();

        // نحافظ على نفس ترتيب coProductIds (الأكتر تطلب مع بعض الأول)
        var ordered2 = coProductIds
            .Select(pid => products.FirstOrDefault(p => p.Id == pid))
            .Where(p => p != null)
            .ToList();

        return Ok(ordered2);
    }

    // ─── GET /api/restaurants/nearby  (public) ──────────────────────────────
    [HttpGet("nearby")]
    public async Task<IActionResult> GetNearby(
        [FromQuery] double lat, [FromQuery] double lng, [FromQuery] double radiusKm = 10)
    {
        var restaurants = await _context.Restaurants.Where(r => r.IsActive && r.IsOpen).ToListAsync();
        var nearby = restaurants
            .Select(r => new {
                r.Id,
                r.Name,
                r.ImageUrl,
                r.Rating,
                r.DeliveryFee,
                r.EstimatedTime,
                r.IsOpen,
                DistanceKm = DeliveryFeeCalculator.GetDistanceKm(lat, lng, r.Latitude, r.Longitude)
            })
            .Where(r => r.DistanceKm <= radiusKm)
            .OrderBy(r => r.DistanceKm).ToList();

        return Ok(nearby);
    }

    // ─── GET /api/restaurants/{id}/reviews  (public) ────────────────────────
    [HttpGet("{id}/reviews")]
    public async Task<IActionResult> GetReviews(int id,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        var exists = await _context.Restaurants.AnyAsync(r => r.Id == id && r.IsActive);
        if (!exists) return NotFound(new { message = "Restaurant not found" });

        var total = await _context.Ratings.CountAsync(r => r.RestaurantId == id);
        var reviews = await _context.Ratings
            .Where(r => r.RestaurantId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
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

    // ─── POST /api/restaurants  [Admin Only] ────────────────────────────────
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
            StoreType = dto.StoreType,
            IsOpen = true,
            IsActive = true,
            OwnerUserId = dto.OwnerUserId,
            CreatedAt = DateTime.UtcNow
        };

        _context.Restaurants.Add(restaurant);
        await _context.SaveChangesAsync();

        if (dto.OwnerUserId.HasValue)
        {
            var owner = await _context.Users.FindAsync(dto.OwnerUserId.Value);
            if (owner != null && owner.Role != "Admin")
            {
                owner.Role = "Restaurant";
                await _context.SaveChangesAsync();
            }
        }

        return CreatedAtAction(nameof(GetById), new { id = restaurant.Id },
            new { restaurant.Id, restaurant.Name });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT api/restaurants/{id}/desktop-update
    // ✅ تأمين: لازم يكون صاحب المطعم ده بالظبط (أو Admin)
    // ─────────────────────────────────────────────────────────────────────────
    [Authorize(Roles = "Restaurant,Admin")]
    [HttpPut("{id}/desktop-update")]
    public async Task<IActionResult> DesktopUpdate(int id, [FromBody] UpdateRestaurantDto dto)
    {
        // تحقق من الـ Ownership
        var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, id, _context);
        if (authError != null) return authError;

        var restaurant = await _context.Restaurants.FindAsync(id);
        if (restaurant == null)
            return NotFound(new { message = "Restaurant not found" });

        restaurant.Name = dto.Name;
        restaurant.Phone = dto.Phone;
        restaurant.Address = dto.Address;
        restaurant.Description = dto.Description;
        restaurant.DeliveryFee = dto.DeliveryFee;
        restaurant.MinOrderAmount = dto.MinOrderAmount;
        restaurant.EstimatedTime = dto.EstimatedTime;
        restaurant.IsOpen = dto.IsOpen;

        if (!string.IsNullOrWhiteSpace(dto.StoreType))
            restaurant.StoreType = dto.StoreType;

        if (dto.Latitude != 0 && dto.Longitude != 0)
        {
            restaurant.Latitude = dto.Latitude;
            restaurant.Longitude = dto.Longitude;
        }

        if (!string.IsNullOrWhiteSpace(dto.ImageUrl))
            restaurant.ImageUrl = dto.ImageUrl;

        if (!string.IsNullOrWhiteSpace(dto.CoverImageUrl))
            restaurant.CoverImageUrl = dto.CoverImageUrl;

        if (User.IsInRole("Admin"))
        {
            restaurant.OwnerUserId = dto.OwnerUserId;
            if (dto.OwnerUserId.HasValue)
            {
                var owner = await _context.Users.FindAsync(dto.OwnerUserId.Value);
                if (owner != null && owner.Role != "Admin")
                    owner.Role = "Restaurant";
            }
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = "Restaurant updated successfully" });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUT api/restaurants/{id}/toggle-status
    // ✅ تأمين: لازم يكون صاحب المطعم ده بالظبط (أو Admin)
    // ─────────────────────────────────────────────────────────────────────────
    [Authorize(Roles = "Restaurant,Admin")]
    [HttpPut("{id}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(int id)
    {
        var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, id, _context);
        if (authError != null) return authError;

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

}

// ─── DTOs ────────────────────────────────────────────────────────────────────
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
    public int? OwnerUserId { get; set; }
    /// <summary>Restaurant | Pharmacy | Grocery | Supermarket | Vegetables | Drinks | Accessories</summary>
    public string StoreType { get; set; } = "Restaurant";
}

public class UpdateRestaurantDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Phone { get; set; }
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal MinOrderAmount { get; set; }
    public int EstimatedTime { get; set; } = 30;
    public bool IsOpen { get; set; }
    public string? ImageUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    /// <summary>Restaurant | Pharmacy | Grocery | Supermarket | Vegetables | Drinks | Accessories</summary>
    public string? StoreType { get; set; }
    public int? OwnerUserId { get; set; }
}