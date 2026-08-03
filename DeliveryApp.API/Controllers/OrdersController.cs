using DeliveryApp.API.Authorization;
using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class OrdersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubService _hubService;
        private readonly IFcmService _fcm;
        private readonly IPointsService _points;
        private readonly INotificationDispatcher _dispatcher;

        public OrdersController(ApplicationDbContext context, IHubService hubService, IFcmService fcm, IPointsService points, INotificationDispatcher dispatcher)
        {
            _context = context;
            _hubService = hubService;
            _fcm = fcm;
            _points = points;
            _dispatcher = dispatcher;
        }

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier
                                          || c.Type == "sub").Value);

        // ─────────────────────────────────────────────
        // POST api/orders
        // ─────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderDto dto)
        {
            var userId = GetUserId();

            if (!dto.Items.Any() && string.IsNullOrWhiteSpace(dto.PrescriptionImageUrl))
                return BadRequest(new { message = "Order must have at least one item or a prescription" });

            var restaurant = await _context.Restaurants
                .FirstOrDefaultAsync(r => r.Id == dto.RestaurantId && r.IsActive && r.IsOpen);
            if (restaurant == null)
                return BadRequest(new { message = "Restaurant not found or closed" });

            if (!string.IsNullOrWhiteSpace(dto.PrescriptionImageUrl) &&
                !restaurant.StoreType.Equals("Pharmacy", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Prescription orders are only for pharmacies" });

            // ✅ لو الأوردر جاي من طلب روشتة اتفق فيه الطرفين على السعر عبر الشات
            PrescriptionRequest? prescriptionRequest = null;
            if (dto.PrescriptionRequestId.HasValue)
            {
                prescriptionRequest = await _context.PrescriptionRequests
                    .FirstOrDefaultAsync(r => r.Id == dto.PrescriptionRequestId.Value && r.CustomerId == userId);

                if (prescriptionRequest == null)
                    return BadRequest(new { message = "طلب الروشتة غير موجود" });
                if (prescriptionRequest.Status != "Confirmed" || prescriptionRequest.AgreedPrice is null)
                    return BadRequest(new { message = "لسه محصلش اتفاق على سعر الروشتة دي" });
                if (prescriptionRequest.RestaurantId != dto.RestaurantId)
                    return BadRequest(new { message = "طلب الروشتة ده مش لنفس الصيدلية" });
            }

            var productIds = dto.Items.Select(i => i.ProductId).ToList();
            var products = productIds.Count == 0
                ? new Dictionary<int, Product>()
                : await _context.Products
                    .Where(p => productIds.Contains(p.Id) && p.IsActive && p.IsAvailable)
                    .ToDictionaryAsync(p => p.Id);

            var variantIds = dto.Items.Where(i => i.VariantId.HasValue).Select(i => i.VariantId!.Value).ToList();
            var variants = variantIds.Count == 0
                ? new Dictionary<int, ProductVariant>()
                : await _context.ProductVariants
                    .Where(v => variantIds.Contains(v.Id) && v.IsActive)
                    .ToDictionaryAsync(v => v.Id);

            foreach (var item in dto.Items)
            {
                if (!products.ContainsKey(item.ProductId))
                    return BadRequest(new { message = $"Product {item.ProductId} not found or unavailable" });
                if (item.VariantId.HasValue && !variants.ContainsKey(item.VariantId.Value))
                    return BadRequest(new { message = $"Variant {item.VariantId} not found" });
            }

            var orderItems = dto.Items.Select(i =>
            {
                decimal unitPrice;
                string? variantName = null;
                if (i.UnitPriceOverride.HasValue)
                    unitPrice = i.UnitPriceOverride.Value;
                else if (i.VariantId.HasValue && variants.TryGetValue(i.VariantId.Value, out var v))
                {
                    unitPrice = v.Price;
                    variantName = v.Name;
                }
                else
                    unitPrice = products[i.ProductId].DiscountedPrice ?? products[i.ProductId].Price;

                return new OrderItem
                {
                    ProductId = i.ProductId,
                    Quantity = i.Quantity,
                    UnitPrice = unitPrice,
                    Notes = i.Notes,
                    VariantId = i.VariantId,
                    VariantName = variantName
                };
            }).ToList();

            // ── حساب سعر التوصيل الفعلي حسب المسافة الحقيقية بين المحل والعميل ──
            // أول FreeRadiusKm كم بسعر المحل الأساسي (restaurant.DeliveryFee)، وبعدها
            // ExtraFeePerKm جنيه على كل كيلومتر زيادة أو جزء منه (قابلين للتعديل من الأدمن).
            var (freeRadiusKm, extraFeePerKm) = await DeliveryFeeCalculator.GetSettingsAsync(_context);
            var distanceKm = DeliveryFeeCalculator.GetDistanceKm(
                restaurant.Latitude, restaurant.Longitude,
                dto.DeliveryLatitude, dto.DeliveryLongitude);
            var deliveryFee = DeliveryFeeCalculator.Calculate(restaurant.DeliveryFee, distanceKm, freeRadiusKm, extraFeePerKm);

            var subTotal = prescriptionRequest?.AgreedPrice ?? orderItems.Sum(i => i.UnitPrice * i.Quantity);
            var total = subTotal + deliveryFee;
            decimal discount = 0;

            // Apply coupon if provided
            Coupon? appliedCoupon = null;
            if (!string.IsNullOrWhiteSpace(dto.CouponCode) || dto.CouponId.HasValue)
            {
                if (dto.CouponId.HasValue)
                    appliedCoupon = await _context.Coupons.FirstOrDefaultAsync(c => c.Id == dto.CouponId);
                else if (!string.IsNullOrWhiteSpace(dto.CouponCode))
                    appliedCoupon = await _context.Coupons.FirstOrDefaultAsync(c => c.Code == dto.CouponCode.ToUpper());

                if (appliedCoupon != null && appliedCoupon.IsActive && (appliedCoupon.ExpiresAt == null || appliedCoupon.ExpiresAt > DateTime.UtcNow))
                {
                    // منع إعادة استخدام الكوبون من نفس المستخدم
                    var alreadyUsed = await _context.UserCoupons.AnyAsync(uc => uc.UserId == userId && uc.CouponId == appliedCoupon.Id);
                    if (!alreadyUsed)
                    {
                        // Check minimum order amount
                        if (appliedCoupon.MinOrderAmount == null || subTotal >= appliedCoupon.MinOrderAmount)
                        {
                            // Check usage limit
                            if (appliedCoupon.UsageLimit == null || appliedCoupon.UsedCount < appliedCoupon.UsageLimit)
                            {
                                // Calculate discount
                                if (appliedCoupon.DiscountType == "Percentage")
                                    discount = (subTotal * appliedCoupon.DiscountValue) / 100;
                                else
                                    discount = appliedCoupon.DiscountValue;

                                // Apply max discount cap if set
                                if (appliedCoupon.MaxDiscount.HasValue && discount > appliedCoupon.MaxDiscount)
                                    discount = appliedCoupon.MaxDiscount.Value;

                                // Increment usage count
                                appliedCoupon.UsedCount++;
                                _context.Coupons.Update(appliedCoupon);

                                // تسجيل استخدام الكوبون للمستخدم
                                _context.UserCoupons.Add(new UserCoupon
                                {
                                    UserId = userId,
                                    CouponId = appliedCoupon.Id,
                                    UsedAt = DateTime.UtcNow
                                });
                            }
                        }
                    }
                }
            }

            total = subTotal + deliveryFee - discount;

            var isPrescriptionOnly = !string.IsNullOrWhiteSpace(dto.PrescriptionImageUrl) && !orderItems.Any();
            if (!isPrescriptionOnly && total < restaurant.MinOrderAmount)
                return BadRequest(new { message = $"Minimum order is {restaurant.MinOrderAmount} EGP" });

            var order = new Order
            {
                CustomerId = userId,
                RestaurantId = dto.RestaurantId,
                Status = "Pending",
                SubTotal = subTotal,
                DeliveryFee = deliveryFee,
                Discount = discount,
                TotalAmount = total,
                DeliveryAddress = dto.DeliveryAddress,
                DeliveryLatitude = dto.DeliveryLatitude,
                DeliveryLongitude = dto.DeliveryLongitude,
                DeliveryNotes = isPrescriptionOnly
                    ? $"[روشتة] {dto.PrescriptionNotes ?? ""}".Trim()
                    : dto.DeliveryNotes,
                PrescriptionImageUrl = dto.PrescriptionImageUrl,
                PrescriptionRequestId = prescriptionRequest?.Id,
                PaymentMethod = dto.PaymentMethod,
                PaymentStatus = "Pending",
                CreatedAt = DateTime.UtcNow,
                OrderItems = orderItems
            };

            _context.Orders.Add(order);
            _context.Payments.Add(new Payment
            {
                Order = order,
                Provider = dto.PaymentMethod,
                Amount = total,
                Currency = "EGP",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            // ✅ الطلب اتحول لأوردر فعلي — نقفل طلب الروشتة عشان محدش يعدل السعر تاني
            if (prescriptionRequest != null)
            {
                prescriptionRequest.Status = "Ordered";
                prescriptionRequest.OrderId = order.Id;
                await _context.SaveChangesAsync();
            }

            var customerLang = await GetUserLanguageAsync(userId);
            var placedNotif = NotificationLocalizer.OrderPlaced(customerLang, restaurant.Name, restaurant.StoreType);

            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = placedNotif.Title,
                Body = placedNotif.Body,
                Type = "OrderPlaced",
                OrderId = order.Id,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            await _fcm.SendToUserAsync(userId, placedNotif.Title, placedNotif.Body,
                new Dictionary<string, string> { ["type"] = "OrderPlaced", ["orderId"] = order.Id.ToString() },
                _context);

            if (restaurant.OwnerUserId.HasValue)
            {
                var ownerLang = await GetUserLanguageAsync(restaurant.OwnerUserId.Value);
                var ownerNotif = NotificationLocalizer.NewOrderForOwner(ownerLang, order.Id);

                _context.Notifications.Add(new Notification
                {
                    UserId = restaurant.OwnerUserId.Value,
                    Title = ownerNotif.Title,
                    Body = ownerNotif.Body,
                    Type = "NewOrder",
                    OrderId = order.Id,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                await _hubService.NotifyUserDirectly(restaurant.OwnerUserId.Value, "NewOrder", order.Id);
                await _fcm.SendToUserAsync(restaurant.OwnerUserId.Value, ownerNotif.Title, ownerNotif.Body,
                    new Dictionary<string, string> { ["type"] = "NewOrder", ["orderId"] = order.Id.ToString() },
                    _context);
            }

            // 🔔 تنبيه الأدمن على أي طلب جديد على المنصة (جرس الأدمن بورتال)
            await _dispatcher.NotifyAdminsAsync(
                "طلب جديد على المنصة",
                $"طلب #{order.Id} من {restaurant.Name} بقيمة {order.TotalAmount:0.##} ج.م",
                "NewOrder",
                order.Id);

            return CreatedAtAction(nameof(GetById), new { id = order.Id }, new
            {
                order.Id,
                order.Status,
                order.TotalAmount,
                order.CreatedAt
            });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/admin/settlements — حسابات السائقين والمطاعم
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpGet("admin/settlements")]
        public async Task<IActionResult> GetSettlements(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] int? driverId,
            [FromQuery] int? restaurantId)
        {
            var fromDate = (from ?? DateTime.UtcNow.AddDays(-30)).Date;
            var toDate = (to ?? DateTime.UtcNow).Date.AddDays(1).AddTicks(-1);

            var query = _context.Orders
                .Where(o => o.Status == "Delivered")
                .Where(o => (o.DeliveredAt ?? o.CreatedAt) >= fromDate && (o.DeliveredAt ?? o.CreatedAt) <= toDate);

            if (driverId.HasValue)
                query = query.Where(o => o.DriverId == driverId);

            if (restaurantId.HasValue)
                query = query.Where(o => o.RestaurantId == restaurantId);

            var orders = await query
                .Select(o => new
                {
                    o.Id,
                    o.DriverId,
                    DriverName = o.Driver != null ? o.Driver.User.FullName : null,
                    o.RestaurantId,
                    RestaurantName = o.Restaurant.Name,
                    o.SubTotal,
                    o.DeliveryFee,
                    o.Discount,
                    o.TotalAmount,
                    o.PaymentMethod,
                    o.PaymentStatus,
                    DeliveredAt = o.DeliveredAt ?? o.CreatedAt
                })
                .ToListAsync();

            var drivers = orders
                .Where(o => o.DriverId.HasValue)
                .GroupBy(o => new { o.DriverId, o.DriverName })
                .Select(g => new
                {
                    DriverId = g.Key.DriverId!.Value,
                    DriverName = g.Key.DriverName ?? "Unknown",
                    OrderCount = g.Count(),
                    CashCollected = g.Where(o => o.PaymentMethod == "Cash").Sum(o => o.TotalAmount),
                    DeliveryEarnings = g.Sum(o => o.DeliveryFee),
                    RestaurantDue = g.Sum(o => o.SubTotal - o.Discount),
                    CardOrders = g.Count(o => o.PaymentMethod != "Cash"),
                    CashOrders = g.Count(o => o.PaymentMethod == "Cash")
                })
                .OrderByDescending(d => d.OrderCount)
                .ToList();

            var restaurants = orders
                .GroupBy(o => new { o.RestaurantId, o.RestaurantName })
                .Select(g => new
                {
                    RestaurantId = g.Key.RestaurantId,
                    RestaurantName = g.Key.RestaurantName,
                    OrderCount = g.Count(),
                    PayoutAmount = g.Sum(o => o.SubTotal - o.Discount),
                    TotalSales = g.Sum(o => o.TotalAmount),
                    DeliveryFees = g.Sum(o => o.DeliveryFee),
                    CashOrders = g.Count(o => o.PaymentMethod == "Cash"),
                    CardOrders = g.Count(o => o.PaymentMethod != "Cash")
                })
                .OrderByDescending(r => r.PayoutAmount)
                .ToList();

            return Ok(new
            {
                from = fromDate,
                to = toDate,
                summary = new
                {
                    TotalOrders = orders.Count,
                    TotalRevenue = orders.Sum(o => o.TotalAmount),
                    TotalRestaurantPayout = orders.Sum(o => o.SubTotal - o.Discount),
                    TotalDeliveryFees = orders.Sum(o => o.DeliveryFee),
                    TotalCashCollected = orders.Where(o => o.PaymentMethod == "Cash").Sum(o => o.TotalAmount)
                },
                drivers,
                restaurants
            });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/admin  — كل الطلبات (لوحة صاحب المنصة)
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin")]
        [HttpGet("admin")]
        public async Task<IActionResult> GetAllAdmin(
            [FromQuery] string? status,
            [FromQuery] string? search,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.Orders.AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(o => o.Status == status);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = int.TryParse(term, out var orderId)
                    ? query.Where(o => o.Id == orderId
                        || o.Customer.FullName.Contains(term)
                        || o.Restaurant.Name.Contains(term))
                    : query.Where(o => o.Customer.FullName.Contains(term)
                        || o.Restaurant.Name.Contains(term));
            }

            var total = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    CustomerName = o.Customer.FullName,
                    RestaurantName = o.Restaurant.Name,
                    o.SubTotal,
                    o.DeliveryFee,
                    o.Discount,
                    o.TotalAmount,
                    o.PaymentMethod,
                    o.PaymentStatus,
                    o.DeliveryAddress,
                    o.DeliveryNotes,
                    ItemCount = o.OrderItems.Count,
                    o.CreatedAt,
                    o.DeliveredAt,
                    o.DriverId,
                    DriverName = o.Driver != null ? o.Driver.User.FullName : null
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = orders });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/{id}
        // ─────────────────────────────────────────────
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var userId = GetUserId();
            var role = User.Claims.First(c => c.Type == ClaimTypes.Role).Value;

            var order = await _context.Orders
                .Where(o => o.Id == id &&
                    (role == "Admin" || o.CustomerId == userId ||
                     (role == "Driver" && o.Driver!.UserId == userId) ||
                     (role == "Restaurant" && o.Restaurant.OwnerUserId == userId)))
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.SubTotal,
                    o.DeliveryFee,
                    o.Discount,
                    o.TotalAmount,
                    o.PaymentMethod,
                    o.PaymentStatus,
                    o.DeliveryAddress,
                    o.DeliveryLatitude,
                    o.DeliveryLongitude,
                    o.DeliveryNotes,
                    o.EstimatedDelivery,
                    o.CancellationReason,
                    o.CreatedAt,
                    o.AcceptedAt,
                    o.PickedUpAt,
                    o.DeliveredAt,
                    Restaurant = new { o.Restaurant.Id, o.Restaurant.Name, o.Restaurant.ImageUrl, o.Restaurant.Phone, o.Restaurant.Latitude, o.Restaurant.Longitude },
                    Driver = o.Driver == null ? null : new
                    {
                        o.Driver.Id,
                        Name = o.Driver.User.FullName,
                        Phone = o.Driver.User.Phone,
                        o.Driver.Rating,
                        o.Driver.CurrentLatitude,
                        o.Driver.CurrentLongitude
                    },
                    Items = o.OrderItems.Select(i => new
                    {
                        i.Id,
                        i.ProductId,
                        ProductName = i.Product.Name,
                        ProductImage = i.Product.ImageUrl,
                        i.Quantity,
                        i.UnitPrice,
                        i.TotalPrice,
                        i.Notes
                    }),
                    // ✅ لو الأوردر اتقيم قبل كده، ابعت التقييم عشان الابلكيشن
                    // يعرضه بدل ما يفضل يطلب من العميل يقيم تاني
                    Rating = o.Rating == null ? null : new
                    {
                        o.Rating.RestaurantRating,
                        o.Rating.DriverRating,
                        o.Rating.FoodRating,
                        o.Rating.Comment,
                        o.Rating.CreatedAt
                    }
                })
                .FirstOrDefaultAsync();

            if (order == null) return NotFound(new { message = "Order not found" });
            return Ok(order);
        }

        // ─────────────────────────────────────────────
        // GET api/orders/my
        // ─────────────────────────────────────────────
        [HttpGet("my")]
        public async Task<IActionResult> MyOrders(
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            var userId = GetUserId();
            var query = _context.Orders.Where(o => o.CustomerId == userId).AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(o => o.Status == status);

            var total = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.TotalAmount,
                    o.CreatedAt,
                    RestaurantName = o.Restaurant.Name,
                    RestaurantImage = o.Restaurant.ImageUrl,
                    ItemCount = o.OrderItems.Count
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = orders });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/driver/my
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpGet("driver/my")]
        public async Task<IActionResult> DriverMyOrders(
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return NotFound(new { message = "Driver profile not found" });

            var query = _context.Orders
                .Where(o => o.DriverId == driver.Id)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(o => o.Status == status);

            var total = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.TotalAmount,
                    o.DeliveryFee,
                    o.DeliveryAddress,
                    o.CreatedAt,
                    o.DeliveredAt,
                    RestaurantName = o.Restaurant.Name
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = orders });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/restaurant/{restaurantId}
        // خاص ببرنامج سطح المكتب للمطعم
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpGet("restaurant/{restaurantId}")]
        public async Task<IActionResult> GetByRestaurant(
            int restaurantId,
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var authError = await RestaurantOwnerAuth.CheckOwnerAsync(User, restaurantId, _context);
            if (authError != null) return authError;

            var query = _context.Orders
                .Where(o => o.RestaurantId == restaurantId)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(o => o.Status == status);

            var total = await query.CountAsync();

            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.SubTotal,
                    o.DeliveryFee,
                    o.Discount,
                    o.TotalAmount,
                    o.PaymentMethod,
                    o.PaymentStatus,
                    o.DeliveryAddress,
                    o.DeliveryNotes,
                    o.EstimatedDelivery,
                    o.CancellationReason,
                    o.CreatedAt,
                    o.AcceptedAt,
                    o.PickedUpAt,
                    o.DeliveredAt,
                    Restaurant = new { o.Restaurant.Id, o.Restaurant.Name, o.Restaurant.ImageUrl, o.Restaurant.Phone },
                    CustomerName = o.Customer.FullName,
                    // CustomerPhone = o.Customer.Phone, // Hidden as per requirement
                    Driver = o.Driver == null ? null : new
                    {
                        o.Driver.Id,
                        Name = o.Driver.User.FullName,
                        Phone = o.Driver.User.Phone,
                        o.Driver.Rating,
                        o.Driver.IsOnline,
                        o.Driver.VehicleType,
                        o.Driver.CurrentLatitude,
                        o.Driver.CurrentLongitude
                    },
                    Items = o.OrderItems.Select(i => new
                    {
                        i.Id,
                        i.ProductId,
                        ProductName = i.Product.Name,
                        ProductImage = i.Product.ImageUrl,
                        i.Quantity,
                        i.UnitPrice,
                        i.TotalPrice,
                        i.Notes
                    })
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, data = orders });
        }

        // ─────────────────────────────────────────────
        // PUT api/orders/{id}/restaurant-status
        // تحديث حالة الأوردر من برنامج المطعم/الصيدلية (لازم تسجيل دخول + إثبات ملكية المحل)
        // ملحوظة أمان: كان [AllowAnonymous] وبدون أي تحقق من ملكية الأوردر —
        // أي حد كان يقدر يغيّر حالة أي أوردر في المنصة. اتصلح هنا.
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Restaurant,Admin")]
        [HttpPut("{id}/restaurant-status")]
        public async Task<IActionResult> UpdateRestaurantStatus(int id, [FromBody] UpdateStatusDto dto)
        {
            var order = await _context.Orders
                .Include(o => o.Customer)
                .Include(o => o.Restaurant)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            // لو صاحب محل (مش أدمن) لازم يتأكد إن الأوردر ده تابع لمحله هو بالظبط
            if (User.IsInRole("Restaurant") && !User.IsInRole("Admin"))
            {
                var userId = GetUserId();
                if (order.Restaurant?.OwnerUserId != userId)
                    return Forbid();
            }

            // المطعم/الصيدلية فقط يقدر يغير: Pending → Accepted/Rejected, Accepted → Preparing, Preparing → ReadyForPickup
            var restaurantTransitions = new Dictionary<string, string[]>
            {
                ["Pending"] = new[] { "Accepted", "Rejected" },
                ["Accepted"] = new[] { "Preparing" },
                ["Preparing"] = new[] { "ReadyForPickup" },
            };

            if (!restaurantTransitions.ContainsKey(order.Status) ||
                !restaurantTransitions[order.Status].Contains(dto.Status))
                return BadRequest(new { message = $"Cannot transition from {order.Status} to {dto.Status}" });

            order.Status = dto.Status;

            if (dto.Status == "Accepted") order.AcceptedAt = DateTime.UtcNow;

            var statusTypes = new[] { "Accepted", "Preparing", "ReadyForPickup", "Rejected" };
            if (statusTypes.Contains(dto.Status))
            {
                var notif = NotificationLocalizer.StatusUpdate(
                    order.Customer.PreferredLanguage, dto.Status, order.Restaurant.StoreType);

                _context.Notifications.Add(new Notification
                {
                    UserId = order.CustomerId,
                    Title = notif.Title,
                    Body = notif.Body,
                    Type = dto.Status == "Rejected" ? "OrderCancelled" : $"Order{dto.Status}",
                    OrderId = order.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            await _hubService.NotifyOrderStatusChanged(order.Id, dto.Status);

            if (statusTypes.Contains(dto.Status))
            {
                var pushNotif = NotificationLocalizer.StatusUpdate(
                    order.Customer.PreferredLanguage, dto.Status, order.Restaurant.StoreType);
                await _fcm.SendToUserAsync(order.CustomerId, pushNotif.Title, pushNotif.Body,
                    new Dictionary<string, string>
                    {
                        ["type"] = dto.Status == "Rejected" ? "OrderCancelled" : $"Order{dto.Status}",
                        ["orderId"] = order.Id.ToString()
                    },
                    _context);
            }

            return Ok(new { message = "Status updated", order.Status });
        }

        // ─────────────────────────────────────────────
        // PUT api/orders/{id}/cancel
        // ─────────────────────────────────────────────
        [HttpPut("{id}/cancel")]
        public async Task<IActionResult> Cancel(int id, [FromBody] CancelOrderDto dto)
        {
            var userId = GetUserId();
            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == id && o.CustomerId == userId);
            if (order == null) return NotFound();

            if (!new[] { "Pending", "Accepted" }.Contains(order.Status))
                return BadRequest(new { message = "Cannot cancel order at this stage" });

            order.Status = "Cancelled";
            order.CancellationReason = dto.Reason;

            var cancelNotif = NotificationLocalizer.StatusUpdate(
                await GetUserLanguageAsync(userId), "Cancelled");

            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = cancelNotif.Title,
                Body = cancelNotif.Body,
                Type = "OrderCancelled",
                OrderId = order.Id,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            await _hubService.NotifyOrderStatusChanged(order.Id, "Cancelled");

            await _fcm.SendToUserAsync(userId, cancelNotif.Title, cancelNotif.Body,
                new Dictionary<string, string> { ["type"] = "OrderCancelled", ["orderId"] = id.ToString() },
                _context);

            return Ok(new { message = "Order cancelled successfully" });
        }

        // ─────────────────────────────────────────────
        // PUT api/orders/{id}/status
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Admin,Driver")]
        [HttpPut("{id}/status")]
        public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusDto dto)
        {
            var order = await _context.Orders
                .Include(o => o.Driver)
                .Include(o => o.Customer)
                .Include(o => o.Restaurant)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var validTransitions = new Dictionary<string, string[]>
            {
                ["Pending"] = new[] { "Accepted", "Rejected" },
                ["Accepted"] = new[] { "Preparing" },
                ["Preparing"] = new[] { "ReadyForPickup" },
                ["ReadyForPickup"] = new[] { "OnTheWay" },
                ["OnTheWay"] = new[] { "Delivered" },
            };

            if (!validTransitions.ContainsKey(order.Status) ||
                !validTransitions[order.Status].Contains(dto.Status))
                return BadRequest(new { message = $"Cannot transition from {order.Status} to {dto.Status}" });

            order.Status = dto.Status;

            switch (dto.Status)
            {
                case "Accepted": order.AcceptedAt = DateTime.UtcNow; break;
                case "OnTheWay": order.PickedUpAt = DateTime.UtcNow; break;
                case "Delivered":
                    order.DeliveredAt = DateTime.UtcNow;
                    order.PaymentStatus = order.PaymentMethod == "Cash" ? "Paid" : order.PaymentStatus;
                    if (order.Driver != null) order.Driver.TotalDeliveries++;
                    break;
            }

            var driverStatusTypes = new[] { "Accepted", "Preparing", "ReadyForPickup", "OnTheWay", "Delivered", "Rejected" };
            if (driverStatusTypes.Contains(dto.Status))
            {
                var notif = NotificationLocalizer.StatusUpdate(
                    order.Customer.PreferredLanguage, dto.Status, order.Restaurant.StoreType);

                _context.Notifications.Add(new Notification
                {
                    UserId = order.CustomerId,
                    Title = notif.Title,
                    Body = notif.Body,
                    Type = dto.Status == "Rejected" ? "OrderCancelled" : $"Order{dto.Status}",
                    OrderId = order.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            if (dto.Status == "Delivered" && order.PointsEarned == 0)
            {
                var earned = _points.CalculateEarnedPoints(order.TotalAmount);
                if (earned > 0)
                {
                    order.PointsEarned = earned;
                    await _context.SaveChangesAsync();
                    await _points.AwardOrderPointsAsync(order.CustomerId, order.Id, order.TotalAmount, _context);
                }
            }

            await _hubService.NotifyOrderStatusChanged(order.Id, dto.Status);

            if (driverStatusTypes.Contains(dto.Status))
            {
                var pushNotif = NotificationLocalizer.StatusUpdate(
                    order.Customer.PreferredLanguage, dto.Status, order.Restaurant.StoreType);
                await _fcm.SendToUserAsync(order.CustomerId, pushNotif.Title, pushNotif.Body,
                    new Dictionary<string, string>
                    {
                        ["type"] = dto.Status == "Rejected" ? "OrderCancelled" : $"Order{dto.Status}",
                        ["orderId"] = order.Id.ToString()
                    },
                    _context);
            }

            // If order is delivered, delete chat messages
            if (dto.Status == "Delivered")
            {
                var messages = await _context.ChatMessages.Where(m => m.OrderId == order.Id).ToListAsync();
                if (messages.Any())
                {
                    _context.ChatMessages.RemoveRange(messages);
                    await _context.SaveChangesAsync();
                }
            }

            return Ok(new { message = "Status updated", order.Status });
        }

        // ─────────────────────────────────────────────
        // GET api/orders/available
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpGet("available")]
        public async Task<IActionResult> GetAvailableOrders()
        {
            var orders = await _context.Orders
                .Where(o => o.Status == "ReadyForPickup" && o.DriverId == null)
                .Select(o => new
                {
                    o.Id,
                    o.TotalAmount,
                    o.DeliveryFee,
                    o.DeliveryAddress,
                    o.DeliveryLatitude,
                    o.DeliveryLongitude,
                    o.CreatedAt,
                    RestaurantName = o.Restaurant.Name,
                    RestaurantLat = o.Restaurant.Latitude,
                    RestaurantLng = o.Restaurant.Longitude,
                    ItemCount = o.OrderItems.Count
                })
                .OrderBy(o => o.CreatedAt)
                .ToListAsync();

            return Ok(orders);
        }

        // ─────────────────────────────────────────────
        // PUT api/orders/{id}/assign-driver
        // ─────────────────────────────────────────────
        [Authorize(Roles = "Driver")]
        [HttpPut("{id}/assign-driver")]
        public async Task<IActionResult> AssignDriver(int id)
        {
            var userId = GetUserId();
            var driver = await _context.Drivers.FirstOrDefaultAsync(d => d.UserId == userId);
            if (driver == null) return Forbid();

            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == id && o.Status == "ReadyForPickup" && o.DriverId == null);
            if (order == null)
                return BadRequest(new { message = "Order not available" });

            order.DriverId = driver.Id;
            await _context.SaveChangesAsync();

            // ← إخطار العميل إن طيار اتعين real-time
            var driverUser = await _context.Users.FindAsync(userId);
            await _hubService.NotifyUserDirectly(order.CustomerId, "DriverAssigned", new
            {
                driverId = driver.Id,
                driverName = driverUser?.FullName,
                orderId = order.Id
            });

            return Ok(new { message = "Order assigned to you", orderId = order.Id });
        }

        private async Task<string> GetUserLanguageAsync(int userId) =>
            await _context.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.PreferredLanguage)
                .FirstOrDefaultAsync() ?? "en";
    }

    // DTOs
    public class PlaceOrderDto
    {
        public int RestaurantId { get; set; }
        public List<OrderItemDto> Items { get; set; } = new();
        public string DeliveryAddress { get; set; } = string.Empty;
        public double DeliveryLatitude { get; set; }
        public double DeliveryLongitude { get; set; }
        public string? DeliveryNotes { get; set; }
        public string? PrescriptionImageUrl { get; set; }
        public string? PrescriptionNotes { get; set; }
        // ✅ لو الأوردر ده جاي بعد ما اتفق العميل وصاحب الصيدلية على سعر عبر الشات
        public int? PrescriptionRequestId { get; set; }
        public string PaymentMethod { get; set; } = "Cash";
        public string? CouponCode { get; set; }
        public int? CouponId { get; set; }
    }

    public class OrderItemDto
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; } = 1;
        public string? Notes { get; set; }
        public int? VariantId { get; set; }
        public decimal? UnitPriceOverride { get; set; }
    }

    public class CancelOrderDto { public string? Reason { get; set; } }
    public class UpdateStatusDto { public string Status { get; set; } = string.Empty; }
}