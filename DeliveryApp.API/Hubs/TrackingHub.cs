using DeliveryApp.API.Models;
using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Hubs
{
    [Authorize]
    public class TrackingHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private readonly IFcmService _fcm;

        public TrackingHub(ApplicationDbContext context, IFcmService fcm)
        {
            _context = context;
            _fcm = fcm;
        }

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

            // سطر واحد ثابت بيتحدث لكل طيار — بلاش Insert في DriverLocations مع كل نبضة موقع
            // (كان ده سبب تضخم الجدول لأنه بيتسجل كل ثوان طول مدة الطلب من غير حد يقراه)
            driver.CurrentLatitude = request.Latitude;
            driver.CurrentLongitude = request.Longitude;
            driver.LastLocationUpdate = DateTime.UtcNow;

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

            // الطرف التاني اللي المفروض يرن عنده
            var calleeUserId = isCustomer ? order.Driver?.UserId : order.CustomerId;

            // 1) نبعت على جروب اليوزر الشخصي (user_{id}) — بيتضاف تلقائي في OnConnectedAsync
            //    فبيوصله حتى لو مش داخل على شاشة الأوردر / مش منضم لـ order_{id}.
            //    (القديم كان order group بس → المكالمة بتضيع لو مش على صفحة التتبع)
            if (calleeUserId.HasValue)
            {
                await Clients.Group($"user_{calleeUserId.Value}").SendAsync("IncomingVoiceCall", new
                {
                    orderId,
                    callerId = userId
                });

                // 2) FCM data push عالي الأولوية — يرن حتى لو الأبليكيشن مقفول (full-screen notif)
                await _fcm.SendToUserAsync(
                    calleeUserId.Value,
                    title: "مكالمة واردة",
                    body: "عندك مكالمة صوتية داخل التطبيق",
                    data: new Dictionary<string, string>
                    {
                        ["type"] = "IncomingCall",
                        ["orderId"] = orderId.ToString(),
                        ["callerId"] = userId.ToString()
                    },
                    db: _context);
            }
            else
            {
                await Clients.OthersInGroup($"order_{orderId}").SendAsync("IncomingVoiceCall", new
                {
                    orderId,
                    callerId = userId
                });
            }
        }

        public async Task AcceptVoiceCall(int orderId)
        {
            var userId = GetUserId();
            await Clients.Group($"order_{orderId}")
                .SendAsync("VoiceCallAccepted", new { orderId, byUserId = userId });
        }

        // ═══ WebRTC signaling relay (SIPSorcery on both ends handles the actual media) ═══
        // الـ Hub هنا بس "بوسطجي" — بيمرر رسائل SDP/ICE بين الطرفين، مش بيلمس محتواها.
        public async Task SendCallOffer(int orderId, string sdp)
        {
            var userId = GetUserId();
            await Clients.OthersInGroup($"order_{orderId}")
                .SendAsync("CallOfferReceived", new { orderId, fromUserId = userId, sdp });
        }

        public async Task SendCallAnswer(int orderId, string sdp)
        {
            var userId = GetUserId();
            await Clients.OthersInGroup($"order_{orderId}")
                .SendAsync("CallAnswerReceived", new { orderId, fromUserId = userId, sdp });
        }

        public async Task SendIceCandidate(int orderId, string candidateJson)
        {
            var userId = GetUserId();
            await Clients.OthersInGroup($"order_{orderId}")
                .SendAsync("IceCandidateReceived", new { orderId, fromUserId = userId, candidateJson });
        }

        public async Task RejectVoiceCall(int orderId)
        {
            var userId = GetUserId();
            await Clients.Group($"order_{orderId}")
                .SendAsync("VoiceCallRejected", new { orderId, byUserId = userId });
        }

        public async Task EndVoiceCall(int orderId)
        {
            var userId = GetUserId();
            await Clients.Group($"order_{orderId}")
                .SendAsync("VoiceCallEnded", new { orderId, byUserId = userId });
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