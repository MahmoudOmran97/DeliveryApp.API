using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    private static string? _cachedToken;
    private static DateTime _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

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
            var projectId = _config["Fcm:ProjectId"];
            if (string.IsNullOrEmpty(projectId))
            {
                _logger.LogWarning("[FCM] ProjectId not configured");
                return;
            }

            var accessToken = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("[FCM] Could not get access token");
                return;
            }

            var payload = new
            {
                message = new
                {
                    token = fcmToken,
                    notification = new { title, body },
                    android = new
                    {
                        priority = "high",
                        notification = new { sound = "default", channel_id = "default" }
                    },
                    apns = new
                    {
                        payload = new { aps = new { sound = "default", badge = 1 } }
                    },
                    data = data ?? new Dictionary<string, string>()
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var client = _http.CreateClient("fcm");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            var url = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";
            var response = await client.PostAsync(url,
                new StringContent(json, Encoding.UTF8, "application/json"));

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[FCM] Send failed: {Code} - {Body}", response.StatusCode, responseBody);
            else
                _logger.LogInformation("[FCM] Sent OK → token ending ...{Suffix}", fcmToken[^Math.Min(6, fcmToken.Length)..]);
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

    // ── OAuth2 via Service Account JWT ──────────────────────────────────────
    private async Task<string?> GetAccessTokenAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var keyPath = _config["Fcm:ServiceAccountPath"];
            if (string.IsNullOrEmpty(keyPath))
            {
                _logger.LogWarning("[FCM] ServiceAccountPath not configured");
                return null;
            }

            // Support relative path (relative to the exe)
            if (!Path.IsPathRooted(keyPath))
                keyPath = Path.Combine(AppContext.BaseDirectory, keyPath);

            if (!File.Exists(keyPath))
            {
                _logger.LogWarning("[FCM] Service account file not found: {Path}", keyPath);
                return null;
            }

            var keyJson = await File.ReadAllTextAsync(keyPath);
            var key = JsonSerializer.Deserialize<ServiceAccountKey>(keyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (key == null || string.IsNullOrEmpty(key.PrivateKey))
            {
                _logger.LogWarning("[FCM] Failed to parse service account JSON");
                return null;
            }

            // Build JWT for Google OAuth2
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
            var payload = Base64UrlEncode(JsonSerializer.Serialize(new
            {
                iss = key.ClientEmail,
                sub = key.ClientEmail,
                aud = "https://oauth2.googleapis.com/token",
                iat = now,
                exp = now + 3600,
                scope = "https://www.googleapis.com/auth/firebase.messaging"
            }));

            var unsigned = $"{header}.{payload}";
            var signature = SignWithRsa(key.PrivateKey, unsigned);
            var jwt = $"{unsigned}.{signature}";

            // Exchange JWT → Access Token
            var client = _http.CreateClient("fcm");
            var resp = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = jwt
                }));

            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FCM] Token exchange failed: {Body}", body);
                return null;
            }

            var tokenData = JsonSerializer.Deserialize<TokenResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _cachedToken = tokenData?.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds((tokenData?.ExpiresIn ?? 3600) - 60);

            return _cachedToken;
        }
        finally { _lock.Release(); }
    }

    private static string SignWithRsa(string privateKeyPem, string data)
    {
        var pem = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("\n", "").Replace("\r", "").Trim();

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pem), out _);
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(data),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Base64UrlEncode(sig);
    }

    private static string Base64UrlEncode(string input) =>
        Base64UrlEncode(Encoding.UTF8.GetBytes(input));

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private class ServiceAccountKey
    {
        public string ClientEmail { get; set; } = "";
        public string PrivateKey { get; set; } = "";
    }

    private class TokenResponse
    {
        public string? AccessToken { get; set; }
        public int ExpiresIn { get; set; }
    }
}