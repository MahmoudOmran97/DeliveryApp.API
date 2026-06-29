using DeliveryApp.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeliveryApp.API.Controllers;

[ApiController]
[Route("api/test")]
[AllowAnonymous]
public class FcmTestController : ControllerBase
{
    private readonly IFcmService _fcm;
    private readonly ILogger<FcmTestController> _logger;

    public FcmTestController(IFcmService fcm, ILogger<FcmTestController> logger)
    {
        _fcm = fcm;
        _logger = logger;
    }

    // GET api/test/fcm/status — check service account + OAuth without sending
    [HttpGet("fcm/status")]
    public async Task<IActionResult> GetStatus()
    {
        var diag = await _fcm.GetDiagnosticsAsync();
        return Ok(diag);
    }

    // POST api/test/fcm
    // Body: { "token": "...", "title": "Test", "body": "Hello" }
    [HttpPost("fcm")]
    public async Task<IActionResult> SendTest([FromBody] FcmTestDto dto)
    {
        _logger.LogInformation("[FCM-TEST] Sending to token ending: ...{Suffix}",
            dto.Token.Length > 6 ? dto.Token[^6..] : dto.Token);

        var sent = await _fcm.SendAsync(dto.Token, dto.Title, dto.Body,
            new Dictionary<string, string> { ["type"] = "Test" });

        return sent
            ? Ok(new { message = "FCM sent successfully" })
            : StatusCode(502, new
            {
                message = "FCM send failed",
                hint = "Call GET /api/test/fcm/status to check service account and OAuth"
            });
    }

    public class FcmTestDto
    {
        public string Token { get; set; } = "";
        public string Title { get; set; } = "Test";
        public string Body { get; set; } = "Test notification";
    }
}