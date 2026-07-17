using AgoraIO.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AgoraController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public AgoraController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        // GET /api/agora/token?channelName=order-123&uid=0
        // channelName: هيبقى غالبًا OrderId أو أي معرف فريد للمكالمة
        // uid: سيبه 0 عادي (Agora هيولّد رقم تلقائي للمستخدم)
        [HttpGet("token")]
        public IActionResult GetToken(string channelName, uint uid = 0)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                return BadRequest("channelName مطلوب");

            var appId = _configuration["Agora:AppId"];
            var appCertificate = _configuration["Agora:AppCertificate"];

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appCertificate))
                return StatusCode(500, "Agora:AppId / Agora:AppCertificate مش متظبطين فى appsettings.json أو الـ User Secrets");

            // مدة صلاحية التوكن: ساعة (كفاية جدًا لمكالمة واحدة بين عميل وسائق)
            uint expireSeconds = 3600;

            var token = RtcTokenBuilder2.buildTokenWithUid(
      appId,
      appCertificate,
      channelName,
      uid,
      RtcTokenBuilder2.Role.RolePublisher,
      expireSeconds,
      expireSeconds);

            return Ok(new
            {
                appId,
                channelName,
                uid,
                token,
                expiresInSeconds = expireSeconds
            });
        }
    }
}
