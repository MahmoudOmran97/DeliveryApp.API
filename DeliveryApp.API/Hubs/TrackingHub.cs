using DeliveryApp.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Hubs
{
    [Authorize]
    public class TrackingHub : Hub
    {
        private readonly ApplicationDbContext _context;

        public TrackingHub(ApplicationDbContext context) => _context = context;

        // ─────────────────────────────────────────────
        // العميل بيدخل غرفة الطلب عشان يتابع الطيار
        // يُستدعى من الـ MAUI بعد ما يعمل طلب
        // ─────────────────────────────────────────────
        public async Task JoinOrderTracking(int orderId)
        {
            var userId = GetUserId();

            // تأكد إن الطلب تبع العميل ده
            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == userId);

            if (order == null)
            {
                await Clients.Caller.SendAsync("Error", "Order not found");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, $"order_{orderId}");
            await Clients.Caller.SendAsync("JoinedTracking", new
            {
                orderId,
                message = "Connected to order tracking"
            });
        }

        // ─────────────────────────────────────────────
        // العميل يخرج من غرفة التتبع
        // ─────────────────────────────────────────────
        public async Task LeaveOrderTracking(int orderId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"order_{orderId}");
        }

        // ─────────────────────────────────────────────
        // الطيار يبعت موقعه — بيتبعت لكل اللي في الغرفة
        // يُستدعى كل 3-5 ثوان من الـ Driver App
        // ─────────────────────────────────────────────
        public async Task UpdateDriverLocation(UpdateLocationRequest request)
        {
            var userId = GetUserId();

            var driver = await _context.Drivers
                .FirstOrDefaultAsync(d => d.UserId == userId);

            if (driver == null)
            {
                await Clients.Caller.SendAsync("Error", "Driver not found");
                return;
            }

            // تحديث الموقع في الداتابيز
            driver.CurrentLatitude = request.Latitude;
            driver.CurrentLongitude = request.Longitude;
            driver.LastLocationUpdate = DateTime.UtcNow;

            // تسجيل الموقع في DriverLocations لو في طلب نشط
            if (request.OrderId.HasValue)
            {
                _context.DriverLocations.Add(new DriverLocation
                {
                    DriverId = driver.Id,
                    OrderId = request.OrderId,
                    Latitude = request.Latitude,
                    Longitude = request.Longitude,
                    Speed = request.Speed,
                    Heading = request.Heading,
                    Timestamp = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            // بعت الموقع لكل العملاء في غرفة الطلب ده
            if (request.OrderId.HasValue)
            {
                await Clients
                    .Group($"order_{request.OrderId}")
                    .SendAsync("DriverLocationUpdated", new
                    {
                        driverId = driver.Id,
                        latitude = request.Latitude,
                        longitude = request.Longitude,
                        speed = request.Speed,
                        heading = request.Heading,
                        timestamp = DateTime.UtcNow
                    });
            }
        }

        // ─────────────────────────────────────────────
        // تحديث حالة الطلب — بيتبعت للعميل فوراً
        // يُستدعى من OrdersController بعد كل تغيير في Status
        // ─────────────────────────────────────────────
        public async Task NotifyOrderStatusChanged(int orderId, string newStatus)
        {
            await Clients
                .Group($"order_{orderId}")
                .SendAsync("OrderStatusChanged", new { orderId, status = newStatus });
        }

        // ─────────────────────────────────────────────
        // اتصل / انقطع
        // ─────────────────────────────────────────────
        public override async Task OnConnectedAsync()
        {
            var userId = GetUserId();
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = GetUserId();
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
            await base.OnDisconnectedAsync(exception);
        }

        // ─────────────────────────────────────────────
        // Helper
        // ─────────────────────────────────────────────
        private int GetUserId()
        {
            var claim = Context.User?.Claims
                .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier
                                  || c.Type == "sub");
            return Convert.ToInt32(claim?.Value);
        }
    }

    public class UpdateLocationRequest
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? Speed { get; set; }
        public double? Heading { get; set; }
        public int? OrderId { get; set; }
    }
}