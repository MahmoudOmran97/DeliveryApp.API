using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

public interface IFcmService
{
    Task<bool> SendAsync(string fcmToken, string title, string body, Dictionary<string, string>? data = null);
    Task SendToUserAsync(int userId, string title, string body,
        Dictionary<string, string>? data = null, ApplicationDbContext? db = null);
    Task<FcmDiagnostics> GetDiagnosticsAsync();
}

public record FcmDiagnostics(
    bool ServiceAccountFound,
    string? ServiceAccountPath,
    bool AccessTokenOk,
    string? LastError);

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

    public async Task<bool> SendAsync(string fcmToken, string title, string body,
        Dictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) return false;

        try
        {
            var projectId = _config["Fcm:ProjectId"];
            if (string.IsNullOrEmpty(projectId))
            {
                _logger.LogWarning("[FCM] ProjectId not configured");
                return false;
            }

            var accessToken = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("[FCM] Could not get access token");
                return false;
            }

            // ✅ KEY FIX: Send as DATA-ONLY message (no "notification" key).
            //
            // FCM rule:
            //   - Notification message (has "notification" key) → Android shows it
            //     automatically when app is BACKGROUND, but does NOT call
            //     onMessageReceived() when app is FOREGROUND.
            //     Result: Plugin.Firebase NotificationReceived event never fires.
            //
            //   - Data message (only "data" key, no "notification") → Android ALWAYS
            //     calls onMessageReceived(), both foreground and background.
            //     Result: Plugin.Firebase NotificationReceived fires every time ✓
            //
            // We put title/body inside the data dict so the MAUI app can read them
            // from args.Notification.Title / args.Notification.Body (Plugin.Firebase
            // maps data["title"] and data["body"] automatically), and also shows its
            // own local notification via NotificationManagerCompat.
            //
            // android.priority = "high" ensures delivery even in Doze mode.

            var dataPayload = new Dictionary<string, string>(data ?? new())
            {
                ["title"] = title,
                ["body"] = body
            };

            var payload = new
            {
                message = new
                {
                    token = fcmToken,
                    notification = new { title, body },
                    android = new
                    {
                        priority = "high"
                    },
                    data = dataPayload
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
            {
                _logger.LogWarning("[FCM] Send failed: {Code} - {Body}", response.StatusCode, responseBody);
                return false;
            }

            _logger.LogInformation("[FCM] Sent OK → token ending ...{Suffix}",
                fcmToken[^Math.Min(6, fcmToken.Length)..]);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FCM] Exception sending notification");
            return false;
        }
    }

    public async Task SendToUserAsync(int userId, string title, string body,
        Dictionary<string, string>? data = null, ApplicationDbContext? db = null)
    {
        if (db == null) return;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (string.IsNullOrEmpty(user?.Fcmtoken))
        {
            _logger.LogWarning("[FCM] User {UserId} has no FCM token in database — notification skipped", userId);
            return;
        }
        await SendAsync(user.Fcmtoken, title, body, data);
    }

    public async Task<FcmDiagnostics> GetDiagnosticsAsync()
    {
        var path = ResolveServiceAccountPath();
        if (path == null)
            return new FcmDiagnostics(false, null, false, "Service account file not found");

        try
        {
            var token = await GetAccessTokenAsync();
            return string.IsNullOrEmpty(token)
                ? new FcmDiagnostics(true, path, false, "OAuth token exchange failed")
                : new FcmDiagnostics(true, path, true, null);
        }
        catch (Exception ex)
        {
            return new FcmDiagnostics(true, path, false, ex.Message);
        }
    }

    // ── OAuth2 via Service Account JWT ──────────────────────────────────────
    private async Task<string?> GetAccessTokenAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            var keyPath = ResolveServiceAccountPath();
            if (keyPath == null)
            {
                _logger.LogWarning("[FCM] Service account file not found. Checked: {Base}firebase-adminsdk.json",
                    AppContext.BaseDirectory);
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

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
            var claimsPayload = Base64UrlEncode(JsonSerializer.Serialize(new
            {
                iss = key.ClientEmail,
                sub = key.ClientEmail,
                aud = "https://oauth2.googleapis.com/token",
                iat = now,
                exp = now + 3600,
                scope = "https://www.googleapis.com/auth/firebase.messaging"
            }));

            var unsigned = $"{header}.{claimsPayload}";
            var signature = SignWithRsa(key.PrivateKey, unsigned);
            var jwt = $"{unsigned}.{signature}";

            var client = _http.CreateClient("fcm");
            var resp = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = jwt
                }));

            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FCM] Token exchange failed: {Body}", respBody);
                return null;
            }

            var tokenData = JsonSerializer.Deserialize<TokenResponse>(respBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _cachedToken = tokenData?.AccessToken;
            _tokenExpiry = DateTime.UtcNow.AddSeconds((tokenData?.ExpiresIn ?? 3600) - 60);

            return _cachedToken;
        }
        finally { _lock.Release(); }
    }

    private string? ResolveServiceAccountPath()
    {
        var configured = _config["Fcm:ServiceAccountPath"];
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(configured))
        {
            candidates.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "firebase-adminsdk.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "wwwroot", "firebase-adminsdk.json"));

        return candidates.FirstOrDefault(File.Exists);
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
        [JsonPropertyName("client_email")]
        public string ClientEmail { get; set; } = "";

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; set; } = "";
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}