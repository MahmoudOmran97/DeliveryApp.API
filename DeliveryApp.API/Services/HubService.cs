using Microsoft.AspNetCore.SignalR;
using DeliveryApp.API.Hubs;

namespace DeliveryApp.API.Services
{
    // ─────────────────────────────────────────────
    // Interface — بيُستخدم في الـ Controllers
    // ─────────────────────────────────────────────
    public interface IHubService
    {
        Task NotifyOrderStatusChanged(int orderId, string status);
        Task NotifyUserDirectly(int userId, string method, object data);
        Task DeleteChatMessages(int orderId);
    }

    // ─────────────────────────────────────────────
    // Implementation
    // ─────────────────────────────────────────────
    public class HubService : IHubService
    {
        private readonly IHubContext<TrackingHub> _hubContext;

        public HubService(IHubContext<TrackingHub> hubContext)
            => _hubContext = hubContext;

        // بيبعت تحديث الحالة لكل اللي في غرفة الطلب
        public async Task NotifyOrderStatusChanged(int orderId, string status)
        {
            await _hubContext.Clients
                .Group($"order_{orderId}")
                .SendAsync("OrderStatusChanged", new { orderId, status });
        }

        // بيبعت رسالة لمستخدم معين مباشرةً (مثلاً: نوتيفيكيشن)
        public async Task NotifyUserDirectly(int userId, string method, object data)
        {
            await _hubContext.Clients
                .Group($"user_{userId}")
                .SendAsync(method, data);
        }

        public async Task DeleteChatMessages(int orderId)
        {
            // Since we don't have direct DB access in HubService easily without inject, 
            // but we can call a method on the hub or just handle it in controller.
            // Actually, HubService usually just sends messages. 
            // Let's keep it simple and handle deletion in Controller.
        }
    }
}