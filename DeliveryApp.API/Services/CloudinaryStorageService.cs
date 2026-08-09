using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeliveryApp.API.Services;

public interface IImageStorageService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string folder);
}

public class CloudinaryStorageService : IImageStorageService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CloudinaryStorageService> _logger;

    public CloudinaryStorageService(IConfiguration config, IHttpClientFactory http, ILogger<CloudinaryStorageService> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, string folder)
    {
        var cloudName = _config["Cloudinary:CloudName"];
        var apiKey = _config["Cloudinary:ApiKey"];
        var apiSecret = _config["Cloudinary:ApiSecret"];

        if (string.IsNullOrWhiteSpace(cloudName) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            throw new InvalidOperationException("إعدادات Cloudinary ناقصة في appsettings.json (CloudName / ApiKey / ApiSecret)");

        var publicId = Path.GetFileNameWithoutExtension(fileName);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var safeContentType = GetSafeContentType(contentType, fileName);

        // ── التوقيع (زي ما هو، مفيش تغيير في المنطق) ──────────────────────
        var paramsToSign = new SortedDictionary<string, string>
        {
            ["folder"] = folder,
            ["public_id"] = publicId,
            ["timestamp"] = timestamp
        };
        var signatureBase = string.Join("&", paramsToSign.Select(p => $"{p.Key}={p.Value}")) + apiSecret;
        var signature = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signatureBase))).ToLowerInvariant();

        // ── قراءة الملف بالكامل في الذاكرة (لازمة عشان نبني الـ body يدويًا) ──
        using var fileMs = new MemoryStream();
        await fileStream.CopyToAsync(fileMs);
        var fileBytes = fileMs.ToArray();

        // ── بناء الـ multipart/form-data يدويًا بالكامل ───────────────────
        // ده بيضمن إن الـ boundary اللي بيتبعت في الـ Content-Type header هو
        // بالظبط نفسه المستخدم في فواصل الـ body. الاعتماد على
        // MultipartFormDataContent بتاعة .NET وتعديل الـ header بعدين مش
        // بيأثر فعليًا على البودي الحقيقي اللي بيتبعت (بيفضل يستخدم القيمة
        // الداخلية القديمة)، وده اللي كان بيخلي Cloudinary مايقراش api_key/
        // signature ويرجع "unsigned upload" error.
        var fields = new Dictionary<string, string>
        {
            ["api_key"] = apiKey,
            ["timestamp"] = timestamp,
            ["folder"] = folder,
            ["public_id"] = publicId,
            ["signature"] = signature
        };

        var (body, boundary) = BuildMultipartBody(fields, fileBytes, "file", fileName, safeContentType);

        var uploadUrl = $"https://api.cloudinary.com/v1_1/{cloudName}/auto/upload";

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        // من غير quotes حوالين الـ boundary عمدًا
        content.Headers.ContentType.Parameters.Add(
            new System.Net.Http.Headers.NameValueHeaderValue("boundary", boundary));

        var client = _http.CreateClient("cloudinary");
        var response = await client.PostAsync(uploadUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Cloudinary] Upload failed: {Code} - {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"فشل رفع الملف على Cloudinary: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var secureUrl = doc.RootElement.GetProperty("secure_url").GetString();

        if (string.IsNullOrWhiteSpace(secureUrl))
            throw new InvalidOperationException("Cloudinary رجّع استجابة من غير secure_url");

        return secureUrl;
    }

    /// <summary>
    /// بيبني جسم الـ multipart/form-data يدويًا كـ bytes، عشان نضمن تطابق كامل
    /// بين الـ boundary المعلن في الـ header والـ boundary الفعلي في البودي.
    /// </summary>
    private static (byte[] Body, string Boundary) BuildMultipartBody(
        Dictionary<string, string> fields,
        byte[] fileBytes,
        string fileFieldName,
        string fileName,
        string fileContentType)
    {
        var boundary = "----CSharpBoundary" + Guid.NewGuid().ToString("N");
        var crlf = "\r\n";

        using var ms = new MemoryStream();

        void WriteText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ms.Write(bytes, 0, bytes.Length);
        }

        foreach (var kv in fields)
        {
            WriteText($"--{boundary}{crlf}");
            WriteText($"Content-Disposition: form-data; name=\"{kv.Key}\"{crlf}{crlf}");
            WriteText($"{kv.Value}{crlf}");
        }

        WriteText($"--{boundary}{crlf}");
        WriteText($"Content-Disposition: form-data; name=\"{fileFieldName}\"; filename=\"{fileName}\"{crlf}");
        WriteText($"Content-Type: {fileContentType}{crlf}{crlf}");
        ms.Write(fileBytes, 0, fileBytes.Length);
        WriteText(crlf);

        WriteText($"--{boundary}--{crlf}");

        return (ms.ToArray(), boundary);
    }

    private static string GetSafeContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType) &&
            System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out _))
        {
            return contentType;
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}