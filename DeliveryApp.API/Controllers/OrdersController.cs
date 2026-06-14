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
        private readonly IHubService _hubService;   // ← جديد

        public OrdersController(ApplicationDbContext context, IHubService hubService)
        {
            _context = context;
            _hubService = hubService;
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

            if (!dto.Items.Any())
                return BadRequest(new { message = "Order must have at least one item" });

            var restaurant = await _context.Restaurants
                .FirstOrDefaultAsync(r => r.Id == dto.RestaurantId && r.IsActive && r.IsOpen);
            if (restaurant == null)
                return BadRequest(new { message = "Restaurant not found or closed" });

            var productIds = dto.Items.Select(i => i.ProductId).ToList();
            var products = await _context.Products
                .Where(p => productIds.Contains(p.Id) && p.IsActive && p.IsAvailable)
                .ToDictionaryAsync(p => p.Id);

            foreach (var item in dto.Items)
                if (!products.ContainsKey(item.ProductId))
                    return BadRequest(new { message = $"Product {item.ProductId} not found or unavailable" });

            var orderItems = dto.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity,
                UnitPrice = products[i.ProductId].DiscountedPrice ?? products[i.ProductId].Price,
                Notes = i.Notes
            }).ToList();

            var subTotal = orderItems.Sum(i => i.UnitPrice * i.Quantity);
            var total = subTotal + restaurant.DeliveryFee;
            decimal discount = 0;

            // Apply coupon if provided
            if (!string.IsNullOrWhiteSpace(dto.CouponCode) || dto.CouponId.HasValue)
            {
                Coupon? coupon = null;
                if (dto.CouponId.HasValue)
                    coupon = await _context.Coupons.FirstOrDefaultAsync(c => c.Id == dto.CouponId);
                else if (!string.IsNullOrWhiteSpace(dto.CouponCode))
                    coupon = await _context.Coupons.FirstOrDefaultAsync(c => c.Code == dto.CouponCode.ToUpper());

                if (coupon != null && coupon.IsActive && (coupon.ExpiresAt == null || coupon.ExpiresAt > DateTime.UtcNow))
                {
                    // Check minimum order amount
                    if (coupon.MinOrderAmount == null || subTotal >= coupon.MinOrderAmount)
                    {
                        // Check usage limit
                        if (coupon.UsageLimit == null || coupon.UsedCount < coupon.UsageLimit)
                        {
                            // Calculate discount
                            if (coupon.DiscountType == "Percentage")
                                discount = (subTotal * coupon.DiscountValue) / 100;
                            else
                                discount = coupon.DiscountValue;

                            // Apply max discount cap if set
                            if (coupon.MaxDiscount.HasValue && discount > coupon.MaxDiscount)
                                discount = coupon.MaxDiscount.Value;

                            // Increment usage count
                            coupon.UsedCount++;
                            _context.Coupons.Update(coupon);
                        }
                    }
                }
            }

            total = subTotal + restaurant.DeliveryFee - discount;

            if (total < restaurant.MinOrderAmount)
                return BadRequest(new { message = $"Minimum order is {restaurant.MinOrderAmount} EGP" });

            var order = new Order
            {
                CustomerId = userId,
                RestaurantId = dto.RestaurantId,
                Status = "Pending",
                SubTotal = subTotal,
                DeliveryFee = restaurant.DeliveryFee,
                Discount = discount,
                TotalAmount = total,
                DeliveryAddress = dto.DeliveryAddress,
                DeliveryLatitude = dto.DeliveryLatitude,
                DeliveryLongitude = dto.DeliveryLongitude,
                DeliveryNotes = dto.DeliveryNotes,
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

            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = "Order Placed!",
                Body = $"Your order from {restaurant.Name} has been placed.",
                Type = "OrderPlaced",
                OrderId = order.Id,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

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
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _context.Orders.AsQueryable();

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
                    o.DeliveredAt
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
                     (role == "Driver" && o.Driver!.UserId == userId)))
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
                    })
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
        [AllowAnonymous]
        [HttpGet("restaurant/{restaurantId}")]
        public async Task<IActionResult> GetByRestaurant(
            int restaurantId,
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
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
        // تحديث حالة الأوردر من برنامج المطعم (بدون تسجيل دخول)
        // ─────────────────────────────────────────────
        [AllowAnonymous]
        [HttpPut("{id}/restaurant-status")]
        public async Task<IActionResult> UpdateRestaurantStatus(int id, [FromBody] UpdateStatusDto dto)
        {
            var order = await _context.Orders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            // المطعم فقط يقدر يغير: Pending → Accepted/Rejected, Accepted → Preparing, Preparing → ReadyForPickup
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

            var notifMap = new Dictionary<string, (string Title, string Body, string Type)>
            {
                ["Accepted"] = ("Order Accepted!", "Your order has been accepted.", "OrderAccepted"),
                ["Preparing"] = ("Preparing Order", "The restaurant is preparing your food.", "OrderPreparing"),
                ["ReadyForPickup"] = ("Order Ready!", "Your order is ready for pickup by driver.", "OrderReadyForPickup"),
                ["Rejected"] = ("Order Rejected", "Sorry, your order was rejected.", "OrderCancelled"),
            };

            if (notifMap.TryGetValue(dto.Status, out var notif))
            {
                _context.Notifications.Add(new Notification
                {
                    UserId = order.CustomerId,
                    Title = notif.Title,
                    Body = notif.Body,
                    Type = notif.Type,
                    OrderId = order.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            await _hubService.NotifyOrderStatusChanged(order.Id, dto.Status);

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

            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Title = "Order Cancelled",
                Body = "Your order has been cancelled.",
                Type = "OrderCancelled",
                OrderId = order.Id,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            // ← إخطار real-time
            await _hubService.NotifyOrderStatusChanged(order.Id, "Cancelled");

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

            var notifMap = new Dictionary<string, (string Title, string Body, string Type)>
            {
                ["Accepted"] = ("Order Accepted!", "Your order has been accepted.", "OrderAccepted"),
                ["Preparing"] = ("Preparing Your Order", "The restaurant is preparing your food.", "OrderPreparing"),
                ["ReadyForPickup"] = ("Order Ready!", "Your order is ready for pickup.", "OrderReadyForPickup"),
                ["OnTheWay"] = ("Driver On The Way!", "Your order is on its way.", "OrderOnTheWay"),
                ["Delivered"] = ("Order Delivered!", "Enjoy your meal!", "OrderDelivered"),
                ["Rejected"] = ("Order Rejected", "Sorry, your order was rejected.", "OrderCancelled"),
            };

            if (notifMap.TryGetValue(dto.Status, out var notif))
            {
                _context.Notifications.Add(new Notification
                {
                    UserId = order.CustomerId,
                    Title = notif.Title,
                    Body = notif.Body,
                    Type = notif.Type,
                    OrderId = order.Id,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            // ← إخطار العميل real-time عبر SignalR
            await _hubService.NotifyOrderStatusChanged(order.Id, dto.Status);

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
        public string PaymentMethod { get; set; } = "Cash";
        public string? CouponCode { get; set; }
        public int? CouponId { get; set; }
    }

    public class OrderItemDto
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public string? Notes { get; set; }
    }

    public class CancelOrderDto { public string? Reason { get; set; } }
    public class UpdateStatusDto { public string Status { get; set; } = string.Empty; }
}