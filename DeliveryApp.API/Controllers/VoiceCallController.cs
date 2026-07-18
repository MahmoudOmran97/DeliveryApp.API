using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using DeliveryApp.API.Hubs;

namespace DeliveryApp.API.Controllers
{
    // ✅ Endpoint REST بسيط بيسمح برفض/إنهاء مكالمة حتى لو مفيش اتصال SignalR شغال —
    // الحالة الأساسية: المستخدم دوس على زرار "رفض" الأحمر في نوتيفيكيشن المكالمة الواردة
    // والتطبيق مقفول تمامًا (الـ process مش شغال، فمفيش Hub connection نقدر نستخدمه).
    // البروسيس بياخد التوكن بتاع JWT المحفوظ على الجهاز ويبعت الطلب مباشرة من الـ
    // BroadcastReceiver في الأندرويد، وبيوصل نفس حدث VoiceCallRejected اللي بيوصل
    // من الـ Hub عادي، فالطرف التاني هيحس بالرفض فورًا برضو.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class VoiceCallController : ControllerBase
    {
        private readonly IHubContext<TrackingHub> _hub;

        public VoiceCallController(IHubContext<TrackingHub> hub)
        {
            _hub = hub;
        }

        private int GetUserId() =>
            int.Parse(User.Claims.First(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier
                                           || c.Type == "sub").Value);

        [HttpPost("reject/{orderId:int}")]
        public async Task<IActionResult> Reject(int orderId)
        {
            var userId = GetUserId();
            await _hub.Clients.Group($"order_{orderId}")
                .SendAsync("VoiceCallRejected", new { orderId, byUserId = userId });
            return Ok();
        }

        [HttpPost("end/{orderId:int}")]
        public async Task<IActionResult> End(int orderId)
        {
            var userId = GetUserId();
            await _hub.Clients.Group($"order_{orderId}")
                .SendAsync("VoiceCallEnded", new { orderId, byUserId = userId });
            return Ok();
        }
    }
}
