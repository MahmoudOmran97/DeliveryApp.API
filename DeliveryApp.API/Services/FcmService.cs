using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

public interface IFcmService
{
    Task SendAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null);
    Task SendToUserAsync(int userId, string title, string body,
        Dictionary<string, string>? data = null, ApplicationDbContext? db = null);
}

public class FcmService : IFcmService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<FcmService> _logger;

    public FcmService(IConfiguration config, IHttpClientFactory http, ILogger<FcmService> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    public async Task SendAsync(string fcmToken, string title, string body,
        Dictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) return;

        try
        {
            var serverKey = _config["Fcm:ServerKey"];
            if (string.IsNullOrEmpty(serverKey))
            {
                _logger.LogWarning("[FCM] ServerKey not configured — skipping push");
                return;
            }

            var payload = new
            {
                to = fcmToken,
                notification = new { title, body, sound = "default" },
                data = data ?? new Dictionary<string, string>(),
                priority = "high",
                content_available = true  // iOS background wakeup
            };

            var json = JsonSerializer.Serialize(payload);
            var client = _http.CreateClient("fcm");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("key", $"={serverKey}");

            var response = await client.PostAsync(
                "https://fcm.googleapis.com/fcm/send",
                new StringContent(json, Encoding.UTF8, "application/json"));

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[FCM] Send failed: {Code} - {Body}", response.StatusCode, responseBody);
            else
                _logger.LogInformation("[FCM] Sent OK to ...{Suffix}", fcmToken[^Math.Min(6, fcmToken.Length)..]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FCM] Exception sending notification");
        }
    }

    public async Task SendToUserAsync(int userId, string title, string body,
        Dictionary<string, string>? data = null, ApplicationDbContext? db = null)
    {
        if (db == null) return;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.Fcmtoken)) return;
        await SendAsync(user.Fcmtoken, title, body, data);
    }
}
