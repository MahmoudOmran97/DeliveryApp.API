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
        // العميل أو الدرايفر بيدخل غرفة الطلب
        // BUG FIX A: كان بيتحقق بس من CustomerId فالدرايفر مش قادر يدخل الـ group
        // ─────────────────────────────────────────────
        public async Task JoinOrderTracking(int orderId)
        {
            var userId = GetUserId();

            // السماح للعميل أو الدرايفر بالانضمام
            var order = await _context.Orders
                .Include(o => o.Driver)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
            {
                await Clients.Caller.SendAsync("Error", "Order not found");
                return;
            }

            bool isCustomer = order.CustomerId == userId;
            bool isDriver = order.Driver?.UserId == userId;

            if (!isCustomer && !isDriver)
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
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
        // خروج من غرفة التتبع
        // ─────────────────────────────────────────────
        public async Task LeaveOrderTracking(int orderId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"order_{orderId}");
        }

        // ─────────────────────────────────────────────
        // الطيار يبعت موقعه
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

            driver.CurrentLatitude = request.Latitude;
            driver.CurrentLongitude = request.Longitude;
            driver.LastLocationUpdate = DateTime.UtcNow;

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
        // تحديث حالة الطلب
        // ─────────────────────────────────────────────
        public async Task NotifyOrderStatusChanged(int orderId, string newStatus)
        {
            await Clients
                .Group($"order_{orderId}")
                .SendAsync("OrderStatusChanged", new { orderId, status = newStatus });
        }

        // ─────────────────────────────────────────────
        // الشات
        // ─────────────────────────────────────────────
        public async Task DeleteChatMessages(int orderId)
        {
            var messages = await _context.ChatMessages.Where(m => m.OrderId == orderId).ToListAsync();
            if (messages.Any())
            {
                _context.ChatMessages.RemoveRange(messages);
                await _context.SaveChangesAsync();
            }
        }

        public async Task SendChatMessage(int orderId, string message)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .Include(o => o.Driver)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
            {
                await Clients.Caller.SendAsync("Error", "Order not found");
                return;
            }

            bool isCustomer = order.CustomerId == userId;
            bool isDriver = order.Driver?.UserId == userId;

            if (!isCustomer && !isDriver)
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            var chatMsg = new ChatMessage
            {
                OrderId = orderId,
                SenderId = userId,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            _context.ChatMessages.Add(chatMsg);
            await _context.SaveChangesAsync();

            await Clients.Group($"order_{orderId}").SendAsync("ChatMessageReceived", new
            {
                orderId,
                senderId = userId,
                message,
                timestamp = chatMsg.Timestamp
            });
        }

        public async Task StartVoiceCall(int orderId)
        {
            var userId = GetUserId();

            var order = await _context.Orders
                .Include(o => o.Driver)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order == null)
            {
                await Clients.Caller.SendAsync("Error", "Order not found");
                return;
            }

            bool isCustomer = order.CustomerId == userId;
            bool isDriver = order.Driver?.UserId == userId;

            if (!isCustomer && !isDriver)
            {
                await Clients.Caller.SendAsync("Error", "Unauthorized");
                return;
            }

            await Clients.Group($"order_{orderId}").SendAsync("IncomingVoiceCall", new
            {
                orderId,
                callerId = userId
            });
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