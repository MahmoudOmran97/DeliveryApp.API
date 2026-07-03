using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeliveryApp.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WebRtcController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public WebRtcController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("ice-servers")]
        public IActionResult GetIceServers()
        {
            var stunServers = _configuration.GetSection("WebRtc:StunServers").Get<string[]>();
            var turnServers = _configuration.GetSection("WebRtc:TurnServers").Get<List<TurnServerConfig>>();

            var iceServers = new List<object>();

            if (stunServers != null)
            {
                foreach (var stun in stunServers)
                {
                    iceServers.Add(new { urls = stun });
                }
            }

            if (turnServers != null)
            {
                foreach (var turn in turnServers)
                {
                    iceServers.Add(new
                    {
                        urls = turn.Urls,
                        username = turn.Username,
                        credential = turn.Credential
                    });
                }
            }

            return Ok(iceServers);
        }

        public class TurnServerConfig
        {
            public string Urls { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string Credential { get; set; } = string.Empty;
        }
    }
}
