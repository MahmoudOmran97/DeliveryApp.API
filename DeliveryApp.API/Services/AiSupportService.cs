using System.Text;
using System.Text.Json;
using DeliveryApp.API.Models;
using Microsoft.EntityFrameworkCore;

namespace DeliveryApp.API.Services;

public class AiReplyResult
{
    public string ReplyText { get; set; } = "";
    public bool Escalated { get; set; }
    public int? CreatedComplaintId { get; set; }
}

public interface IAiSupportService
{
    Task<AiReplyResult> GetReplyAsync(SupportSession session, IReadOnlyList<SupportMessage> history, User customer, string? language = null);
}

// ─────────────────────────────────────────────────────────────────────────
// بيكلم واحد من اتنين حسب شكل الـ ApiKey المتخزن في AiSettings (بيتحكم فيه
// الأدمن من شاشة "إعدادات الـ AI") — من غير ما الأدمن يحتاج يختار المزوّد
// يدويًا:
//
//   • مفتاح Google AI Studio (بيبدأ بـ "AIza" أو "AQ.")
//       → بيتبعت لـ Gemini native REST API
//         (https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent)
//       → الموديل المفروض يتكتب بصيغة Gemini الأصلية زي "gemini-2.0-flash"
//         (من غير بادئة "google/" ومن غير أرقام إصدار زي "-001").
//
//   • أي مفتاح تاني (زي مفاتيح OpenRouter اللي بتبدأ بـ "sk-or-v1-")
//       → بيتبعت لـ OpenRouter بصيغة متوافقة مع OpenAI Chat Completions API
//         (https://openrouter.ai/api/v1/chat/completions)
//       → الموديل بيتحدد كـ slug بتاع OpenRouter زي "openai/gpt-4o-mini" أو
//         "anthropic/claude-3.5-sonnet" أو "google/gemini-2.0-flash-001".
//
// في الحالتين بيديله نفس 5 أدوات (function tools) بنفس المعنى، لكن بصيغة
// كل مزوّد الخاصة بيه:
//   1) get_customer_orders → آخر أوردرات العميل
//   2) track_order         → تفاصيل وحالة أوردر معين
//   3) cancel_order        → إلغاء أوردر لسه Pending/Accepted
//   4) create_complaint    → يسجل شكوى رسمية باسم العميل في جدول Complaints
//   5) escalate_to_admin   → يحول الشات لأدمن حقيقي ويبعت إشعار للأدمن
// بشات مفتوح مع العميل ده (Notification.ActionUrl = "supportchat/{sessionId}").
//
// منطق تنفيذ الأدوات نفسه (اللي بيلمس الداتابيز) موحّد في ExecuteToolAsync
// عشان مايتكررش بين المزودين.
// ─────────────────────────────────────────────────────────────────────────
public class AiSupportService : IAiSupportService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IHubService _hub;
    private readonly ILogger<AiSupportService> _logger;

    private const string OpenRouterUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public AiSupportService(ApplicationDbContext context, IHttpClientFactory httpFactory,
        INotificationDispatcher dispatcher, IHubService hub, ILogger<AiSupportService> logger)
    {
        _context = context;
        _httpFactory = httpFactory;
        _dispatcher = dispatcher;
        _hub = hub;
        _logger = logger;
    }

    // ── صيغة OpenAI function-calling (اللي OpenRouter بيتوقعها في "tools") ──
    private static readonly object[] OpenAiTools = new object[]
    {
        new
        {
            type = "function",
            function = new
            {
                name = "get_customer_orders",
                description = "هات آخر أوردرات العميل (رقم الأوردر، اسم المحل، الحالة، الإجمالي، والتاريخ). استخدمها الأول لما العميل يسأل عن أوردر من غير ما يديك رقمه (زي \"فين أوردري\" أو \"طلبي اتأخر\") عشان تعرف تحدد هو بيقصد أوردر رقم كام.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        onlyActive = new { type = "boolean", description = "لو true هات بس الأوردرات اللي لسه شغالة (مش Delivered ولا Cancelled ولا Rejected). افتراضي false يعني هات آخر الأوردرات كلها." }
                    },
                    required = Array.Empty<string>()
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "track_order",
                description = "هات تفاصيل وحالة أوردر معين بالظبط (رقمه، حالته الحالية، اسم المحل، اسم وتليفون السائق لو اتعين، والوقت المتوقع للتوصيل). استخدمها لما يبقى معاك رقم الأوردر (من العميل أو من get_customer_orders) وعايز تعرف آخر تحديث عليه.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        orderId = new { type = "integer", description = "رقم الأوردر المطلوب تتبعه" }
                    },
                    required = new[] { "orderId" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "cancel_order",
                description = "ألغي أوردر العميل فعليًا. بينفع بس للأوردرات لسه في حالة Pending (لسه ماتقبلش) أو Accepted (اتقبل بس لسه ما دخلش التحضير). لو الأوردر بعد كده (Preparing أو ReadyForPickup أو OnTheWay أو Delivered) الإلغاء هيترفض تلقائيًا، وفي الحالة دي اقترح على العميل إنه يعمل شكوى (create_complaint) أو التحويل لأدمن بدل ما تحاول تاني.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        orderId = new { type = "integer", description = "رقم الأوردر المطلوب إلغاؤه" },
                        reason = new { type = "string", description = "سبب الإلغاء زي ما قاله العميل" }
                    },
                    required = new[] { "orderId" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "create_complaint",
                description = "سجّل شكوى رسمية للعميل عن مشكلة حقيقية ذكرها (طلب متأخر، منتج فاسد أو ناقص، طلب استرجاع/استبدال منتج بعد التوصيل، مندوب أساء التعامل، خصم غلط، إلخ). دي نفس الطريقة اللي بيتسجل بيها طلب الاسترجاع (Return) لأن مفيش نظام استرجاع منفصل — الأدمن بيراجع الشكوى ويقرر الاسترداد. استخدمها لما تكون متأكد إن العميل بيشتكي أو عايز يسترجع فعلاً مش بس بيسأل سؤال عادي.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        subject = new { type = "string", description = "عنوان مختصر للشكوى" },
                        description = new { type = "string", description = "تفاصيل الشكوى زي ما فهمتها من كلام العميل" },
                        orderId = new { type = "integer", description = "رقم الطلب المرتبط بالشكوى لو معروف (اختياري)" }
                    },
                    required = new[] { "subject", "description" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "escalate_to_admin",
                description = "حوّل الشات لأدمن حقيقي لما الموضوع معقد أو محتاج قرار بشري أو العميل طلب صراحة يتكلم مع حد حقيقي.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        reason = new { type = "string", description = "سبب مختصر لتحويل الشات للأدمن" }
                    },
                    required = new[] { "reason" }
                }
            }
        }
    };

    // ── نفس الأدوات بالظبط لكن بصيغة Gemini (functionDeclarations من غير
    //    الـ wrapper بتاع "type":"function"/"function" اللي OpenAI بتستخدمه) ──
    private static readonly object[] GeminiFunctionDeclarations = new object[]
    {
        new
        {
            name = "get_customer_orders",
            description = "هات آخر أوردرات العميل (رقم الأوردر، اسم المحل، الحالة، الإجمالي، والتاريخ). استخدمها الأول لما العميل يسأل عن أوردر من غير ما يديك رقمه (زي \"فين أوردري\" أو \"طلبي اتأخر\") عشان تعرف تحدد هو بيقصد أوردر رقم كام.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    onlyActive = new { type = "boolean", description = "لو true هات بس الأوردرات اللي لسه شغالة (مش Delivered ولا Cancelled ولا Rejected). افتراضي false يعني هات آخر الأوردرات كلها." }
                },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "track_order",
            description = "هات تفاصيل وحالة أوردر معين بالظبط (رقمه، حالته الحالية، اسم المحل، اسم وتليفون السائق لو اتعين، والوقت المتوقع للتوصيل). استخدمها لما يبقى معاك رقم الأوردر (من العميل أو من get_customer_orders) وعايز تعرف آخر تحديث عليه.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    orderId = new { type = "integer", description = "رقم الأوردر المطلوب تتبعه" }
                },
                required = new[] { "orderId" }
            }
        },
        new
        {
            name = "cancel_order",
            description = "ألغي أوردر العميل فعليًا. بينفع بس للأوردرات لسه في حالة Pending (لسه ماتقبلش) أو Accepted (اتقبل بس لسه ما دخلش التحضير). لو الأوردر بعد كده (Preparing أو ReadyForPickup أو OnTheWay أو Delivered) الإلغاء هيترفض تلقائيًا، وفي الحالة دي اقترح على العميل إنه يعمل شكوى (create_complaint) أو التحويل لأدمن بدل ما تحاول تاني.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    orderId = new { type = "integer", description = "رقم الأوردر المطلوب إلغاؤه" },
                    reason = new { type = "string", description = "سبب الإلغاء زي ما قاله العميل" }
                },
                required = new[] { "orderId" }
            }
        },
        new
        {
            name = "create_complaint",
            description = "سجّل شكوى رسمية للعميل عن مشكلة حقيقية ذكرها (طلب متأخر، منتج فاسد أو ناقص، طلب استرجاع/استبدال منتج بعد التوصيل، مندوب أساء التعامل، خصم غلط، إلخ). دي نفس الطريقة اللي بيتسجل بيها طلب الاسترجاع (Return) لأن مفيش نظام استرجاع منفصل — الأدمن بيراجع الشكوى ويقرر الاسترداد. استخدمها لما تكون متأكد إن العميل بيشتكي أو عايز يسترجع فعلاً مش بس بيسأل سؤال عادي.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    subject = new { type = "string", description = "عنوان مختصر للشكوى" },
                    description = new { type = "string", description = "تفاصيل الشكوى زي ما فهمتها من كلام العميل" },
                    orderId = new { type = "integer", description = "رقم الطلب المرتبط بالشكوى لو معروف (اختياري)" }
                },
                required = new[] { "subject", "description" }
            }
        },
        new
        {
            name = "escalate_to_admin",
            description = "حوّل الشات لأدمن حقيقي لما الموضوع معقد أو محتاج قرار بشري أو العميل طلب صراحة يتكلم مع حد حقيقي.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    reason = new { type = "string", description = "سبب مختصر لتحويل الشات للأدمن" }
                },
                required = new[] { "reason" }
            }
        }
    };

    // مفاتيح Google AI Studio (Gemini) بتبدأ إما بـ "AIza" (الصيغة القديمة)
    // أو "AQ." (صيغة أحدث بتصدرها جوجل دلوقتي لبعض الحسابات). أي حاجة تانية
    // (زي مفاتيح OpenRouter اللي بتبدأ بـ "sk-or-v1-") بتتعامل كـ OpenRouter.
    private static bool IsGoogleKey(string apiKey) =>
        apiKey.StartsWith("AIza", StringComparison.OrdinalIgnoreCase) ||
        apiKey.StartsWith("AQ.", StringComparison.OrdinalIgnoreCase);

    public async Task<AiReplyResult> GetReplyAsync(SupportSession session, IReadOnlyList<SupportMessage> history, User customer, string? language = null)
    {
        var settings = await _context.AiSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new AiReplyResult
            {
                ReplyText = "خدمة المساعد الذكي متوقفة دلوقتي، حاول تاني بعدين أو اطلب التحويل لأدمن."
            };
        }

        var languageInstruction = language?.ToLowerInvariant() switch
        {
            "en" => "IMPORTANT: Always reply in the same language the customer's message is written in. " +
                    "If the customer's message is written in English, you must reply in English (not Arabic). " +
                    "If it's ambiguous or too short to tell, default to English since that's the app's current language.",
            "ar" => "IMPORTANT: رد دايمًا بنفس لغة رسالة العميل. لو العميل كتب بالإنجليزي رد بالإنجليزي، " +
                    "ولو كتب بالعربي رد بالعربي. لو الرسالة قصيرة جدًا ومش واضح منها اللغة، ردّ بالعربي لأنها لغة التطبيق الحالية.",
            _ => "IMPORTANT: Always reply in the same language the customer's message is written in, regardless of any other instructions above."
        };
        var systemPrompt = (settings.SystemPrompt ?? "") + "\n\n" + languageInstruction;

        var apiKey = settings.ApiKey!.Trim();
        var result = new AiReplyResult();

        return IsGoogleKey(apiKey)
            ? await RunGeminiAsync(settings, apiKey, systemPrompt, history, session, customer, language, result)
            : await RunOpenRouterAsync(settings, apiKey, systemPrompt, history, session, customer, language, result);
    }

    // ═════════════════════════════ OpenRouter ═══════════════════════════════
    private async Task<AiReplyResult> RunOpenRouterAsync(AiSettings settings, string apiKey, string systemPrompt,
        IReadOnlyList<SupportMessage> history, SupportSession session, User customer, string? language, AiReplyResult result)
    {
        var messages = new List<object>
        {
            new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt }
        };

        messages.AddRange(history
            .Where(m => m.SenderRole is "Customer" or "AI")
            .OrderBy(m => m.CreatedAt)
            .Select(m => (object)new Dictionary<string, object>
            {
                ["role"] = m.SenderRole == "Customer" ? "user" : "assistant",
                ["content"] = m.Message
            }));

        var client = _httpFactory.CreateClient();

        // أقصى 4 دورات: دلوقتي في أدوات ممكن تتسلسل (مثلاً get_customer_orders
        // عشان يعرف رقم الأوردر، وبعدين track_order أو cancel_order بيه)، فمحتاجين
        // مساحة أكبر من دورتين عشان الـ AI يقدر يستخدم أكتر من أداة قبل الرد النهائي.
        for (int round = 0; round < 4; round++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = settings.Model,
                ["max_tokens"] = settings.MaxTokens,
                ["messages"] = messages,
                ["tools"] = OpenAiTools
            };

            var request = new HttpRequestMessage(HttpMethod.Post, OpenRouterUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            // OpenRouter بيستحسن الهيدرز دي (اختيارية) عشان يظهر التطبيق في لوحة تحكمهم
            request.Headers.Add("HTTP-Referer", "https://tawseela.app");
            request.Headers.Add("X-Title", "Taly Support Chat");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI support (OpenRouter): فشل الاتصال");
                result.ReplyText = "⚠️ في مشكلة في الاتصال بخدمة المساعد الذكي، حاول تاني كمان شوية.";
                return result;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AI support (OpenRouter): رجع {Status} — {Body}", response.StatusCode, responseBody);
                result.ReplyText = "⚠️ في مشكلة في المساعد الذكي دلوقتي، جرب تاني كمان شوية أو اطلب التحويل لأدمن.";
                return result;
            }

            using var doc = JsonDocument.Parse(responseBody);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                _logger.LogError("AI support (OpenRouter): رد بدون choices — {Body}", responseBody);
                result.ReplyText = "⚠️ حصل خطأ في رد المساعد الذكي، حاول تاني.";
                return result;
            }

            var message = choices[0].GetProperty("message");
            string? textPart = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var toolCalls = new List<(string id, string name, string argsJson)>();
            if (message.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcArr.EnumerateArray())
                {
                    var fn = tc.GetProperty("function");
                    toolCalls.Add((
                        tc.GetProperty("id").GetString()!,
                        fn.GetProperty("name").GetString()!,
                        fn.GetProperty("arguments").GetString() ?? "{}"
                    ));
                }
            }

            if (toolCalls.Count == 0)
            {
                result.ReplyText = textPart ?? "👍";
                return result;
            }

            // نضيف رسالة الـ assistant بمحتواها الخام (فيها tool_calls) عشان نبعت
            // ردود الأدوات (role=tool) بعدها بشكل صحيح حسب بروتوكول OpenAI.
            messages.Add(JsonSerializer.Deserialize<object>(message.GetRawText())!);

            foreach (var (id, name, argsJson) in toolCalls)
            {
                JsonDocument? argsDoc = null;
                try { argsDoc = JsonDocument.Parse(argsJson); } catch { /* args غير صالحة */ }
                var args = argsDoc?.RootElement ?? default;

                var toolResultText = await ExecuteToolAsync(name, args, session, customer, language, result);
                argsDoc?.Dispose();

                messages.Add(new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = id,
                    ["content"] = toolResultText
                });
            }

            // لو الشات اتحول لأدمن، مفيش داعي نرجع نسأل الـ AI تاني — بس ناخد رد بسيط ونوقف
            if (result.Escalated)
            {
                result.ReplyText = textPart ?? "تم تحويل طلبك لأحد أعضاء فريق الدعم، هيتواصل معاك قريبًا 🙏";
                return result;
            }
            // كمل اللوب عشان ناخد الرد النهائي بعد تنفيذ الأداة (مثلاً بعد create_complaint)
        }

        result.ReplyText = "تم تسجيل طلبك، هنتابعه معاك.";
        return result;
    }

    // ═══════════════════════════ Google Gemini (native) ══════════════════════
    private async Task<AiReplyResult> RunGeminiAsync(AiSettings settings, string apiKey, string systemPrompt,
        IReadOnlyList<SupportMessage> history, SupportSession session, User customer, string? language, AiReplyResult result)
    {
        // لو الأدمن كتب الموديل بصيغة OpenRouter (زي "google/gemini-2.0-flash-001")
        // بمصادفة، بنشيل بادئة "google/" على الأقل عشان نجرب. الأفضل إنه يكتب
        // اسم الموديل بصيغة Gemini الأصلية زي "gemini-2.0-flash".
        var modelName = settings.Model.StartsWith("google/", StringComparison.OrdinalIgnoreCase)
            ? settings.Model["google/".Length..]
            : settings.Model;

        var contents = new List<object>();
        contents.AddRange(history
            .Where(m => m.SenderRole is "Customer" or "AI")
            .OrderBy(m => m.CreatedAt)
            .Select(m => (object)new Dictionary<string, object>
            {
                ["role"] = m.SenderRole == "Customer" ? "user" : "model",
                ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = m.Message } }
            }));

        var client = _httpFactory.CreateClient();
        var url = $"{GeminiBaseUrl}/{Uri.EscapeDataString(modelName)}:generateContent";

        for (int round = 0; round < 4; round++)
        {
            var body = new Dictionary<string, object>
            {
                ["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new object[] { new Dictionary<string, object> { ["text"] = systemPrompt } }
                },
                ["contents"] = contents,
                ["tools"] = new object[] { new Dictionary<string, object> { ["functionDeclarations"] = GeminiFunctionDeclarations } },
                ["generationConfig"] = new Dictionary<string, object> { ["maxOutputTokens"] = settings.MaxTokens }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-goog-api-key", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI support (Gemini): فشل الاتصال");
                result.ReplyText = "⚠️ في مشكلة في الاتصال بخدمة المساعد الذكي، حاول تاني كمان شوية.";
                return result;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AI support (Gemini): رجع {Status} — {Body}", response.StatusCode, responseBody);
                result.ReplyText = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "⚠️ المساعد الذكي وصل للحد الأقصى من الطلبات المسموح بيه دلوقتي، جرب تاني بعد شوية أو اطلب التحويل لأدمن."
                    : "⚠️ في مشكلة في المساعد الذكي دلوقتي، جرب تاني كمان شوية أو اطلب التحويل لأدمن.";
                return result;
            }

            using var doc = JsonDocument.Parse(responseBody);

            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                _logger.LogError("AI support (Gemini): رد بدون candidates — {Body}", responseBody);
                result.ReplyText = "⚠️ حصل خطأ في رد المساعد الذكي، حاول تاني.";
                return result;
            }

            var contentEl = candidates[0].GetProperty("content");
            var parts = contentEl.TryGetProperty("parts", out var p) ? p : default;

            string? textPart = null;
            var functionCalls = new List<(string name, JsonElement args)>();
            if (parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        textPart = (textPart ?? "") + t.GetString();

                    if (part.TryGetProperty("functionCall", out var fc))
                    {
                        var fname = fc.GetProperty("name").GetString()!;
                        var fargs = fc.TryGetProperty("args", out var fa) ? fa : default;
                        functionCalls.Add((fname, fargs));
                    }
                }
            }

            if (functionCalls.Count == 0)
            {
                result.ReplyText = textPart ?? "👍";
                return result;
            }

            // نضيف دور الـ model زي ما رجع بالظبط (فيه الـ functionCall) عشان نقدر
            // نبعت بعده ردود الأدوات (functionResponse) بشكل صحيح.
            contents.Add(new Dictionary<string, object>
            {
                ["role"] = "model",
                ["parts"] = JsonSerializer.Deserialize<object>(parts.GetRawText())!
            });

            var responseParts = new List<object>();
            foreach (var (name, args) in functionCalls)
            {
                var toolResultText = await ExecuteToolAsync(name, args, session, customer, language, result);

                responseParts.Add(new Dictionary<string, object>
                {
                    ["functionResponse"] = new Dictionary<string, object>
                    {
                        ["name"] = name,
                        // Gemini بيتوقع "response" ككائن JSON (Struct)، فبنلفه في حقل result
                        ["response"] = new Dictionary<string, object> { ["result"] = toolResultText }
                    }
                });
            }
            contents.Add(new Dictionary<string, object> { ["role"] = "user", ["parts"] = responseParts });

            if (result.Escalated)
            {
                result.ReplyText = textPart ?? "تم تحويل طلبك لأحد أعضاء فريق الدعم، هيتواصل معاك قريبًا 🙏";
                return result;
            }
        }

        result.ReplyText = "تم تسجيل طلبك، هنتابعه معاك.";
        return result;
    }

    // ═════════════════ تنفيذ الأداة الفعلي — مشترك بين المزودين ═════════════════
    private async Task<string> ExecuteToolAsync(string name, JsonElement args, SupportSession session, User customer, string? language, AiReplyResult result)
    {
        string toolResultText;
        switch (name)
        {
            case "get_customer_orders":
                {
                    var onlyActive = args.ValueKind == JsonValueKind.Object &&
                        args.TryGetProperty("onlyActive", out var oa) && oa.ValueKind == JsonValueKind.True;

                    var activeStatuses = new[] { "Pending", "Accepted", "Preparing", "ReadyForPickup", "OnTheWay" };

                    var ordersQuery = _context.Orders
                        .Where(o => o.CustomerId == customer.Id);

                    if (onlyActive)
                        ordersQuery = ordersQuery.Where(o => activeStatuses.Contains(o.Status));

                    var orders = await ordersQuery
                        .OrderByDescending(o => o.CreatedAt)
                        .Take(10)
                        .Select(o => new
                        {
                            o.Id,
                            Store = o.Restaurant.Name,
                            o.Status,
                            o.TotalAmount,
                            CreatedAt = o.CreatedAt
                        })
                        .ToListAsync();

                    toolResultText = orders.Count == 0
                        ? "العميل مفيش عنده أي أوردرات."
                        : JsonSerializer.Serialize(orders);
                    break;
                }
            case "track_order":
                {
                    var trackOrderId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("orderId", out var toId) && toId.ValueKind == JsonValueKind.Number
                        ? toId.GetInt32() : (int?)null;

                    if (trackOrderId == null)
                    { toolResultText = "رقم الأوردر مطلوب."; break; }

                    var trackedOrder = await _context.Orders
                        .Where(o => o.Id == trackOrderId && o.CustomerId == customer.Id)
                        .Select(o => new
                        {
                            o.Id,
                            Store = o.Restaurant.Name,
                            o.Status,
                            o.TotalAmount,
                            o.EstimatedDeliveryMin,
                            o.EstimatedDeliveryMax,
                            DriverName = o.Driver != null ? o.Driver.User.FullName : null,
                            DriverPhone = o.Driver != null ? o.Driver.User.Phone : null,
                            o.CreatedAt,
                            o.AcceptedAt,
                            o.PickedUpAt,
                            o.DeliveredAt,
                            o.CancellationReason
                        })
                        .FirstOrDefaultAsync();

                    toolResultText = trackedOrder == null
                        ? "مفيش أوردر بالرقم ده لنفس العميل ده."
                        : JsonSerializer.Serialize(trackedOrder);
                    break;
                }
            case "cancel_order":
                {
                    var cancelOrderId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("orderId", out var coId) && coId.ValueKind == JsonValueKind.Number
                        ? coId.GetInt32() : (int?)null;
                    var cancelReason = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reason", out var cr) ? cr.GetString() : null;

                    if (cancelOrderId == null)
                    { toolResultText = "رقم الأوردر مطلوب."; break; }

                    var orderToCancel = await _context.Orders
                        .FirstOrDefaultAsync(o => o.Id == cancelOrderId && o.CustomerId == customer.Id);

                    if (orderToCancel == null)
                    { toolResultText = "مفيش أوردر بالرقم ده لنفس العميل ده."; break; }

                    if (!new[] { "Pending", "Accepted" }.Contains(orderToCancel.Status))
                    {
                        toolResultText = $"معذرة، مينفعش نلغي الأوردر ده — حالته دلوقتي \"{orderToCancel.Status}\" وده متأخر عن مرحلة الإلغاء. اقترح على العميل يعمل شكوى أو التحويل لأدمن.";
                        break;
                    }

                    orderToCancel.Status = "Cancelled";
                    orderToCancel.CancellationReason = string.IsNullOrWhiteSpace(cancelReason) ? "ألغاه العميل عن طريق المساعد الذكي" : cancelReason;
                    await _context.SaveChangesAsync();

                    var lang = NotificationLocalizer.NormalizeLang(language);
                    var cancelNotif = NotificationLocalizer.StatusUpdate(lang, "Cancelled");
                    await _dispatcher.NotifyUserAsync(customer.Id, cancelNotif.Title, cancelNotif.Body,
                        "OrderCancelled", orderToCancel.Id);
                    await _hub.NotifyOrderStatusChanged(orderToCancel.Id, "Cancelled");

                    toolResultText = $"تم إلغاء الأوردر رقم {orderToCancel.Id} بنجاح.";
                    break;
                }
            case "create_complaint":
                {
                    var subject = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("subject", out var s) ? s.GetString() ?? "شكوى بدون عنوان" : "شكوى بدون عنوان";
                    var description = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    int? orderId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("orderId", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : null;

                    var complaint = new Complaint
                    {
                        CustomerId = customer.Id,
                        OrderId = orderId,
                        SupportSessionId = session.Id,
                        Subject = subject,
                        Description = description,
                        Status = "Open",
                        Source = "AI",
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.Complaints.Add(complaint);
                    await _context.SaveChangesAsync();

                    result.CreatedComplaintId = complaint.Id;

                    await _dispatcher.NotifyAdminsAsync(
                        "شكوى جديدة من العميل 📝",
                        $"{customer.FullName}: {subject}",
                        "Complaint",
                        orderId,
                        $"complaint/{complaint.Id}");

                    toolResultText = $"تم تسجيل الشكوى رقم {complaint.Id} بنجاح.";
                    break;
                }
            case "escalate_to_admin":
                {
                    var reason = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";

                    session.Status = "Escalated";
                    await _context.SaveChangesAsync();
                    result.Escalated = true;

                    await _dispatcher.NotifyAdminsAsync(
                        "شات دعم محتاج تدخل أدمن 🆘",
                        $"{customer.FullName}: {reason}",
                        "SupportEscalated",
                        null,
                        $"supportchat/{session.Id}");

                    await _hub.NotifyUserDirectly(customer.Id, "SupportEscalated", new { session.Id });

                    toolResultText = "تم تحويل الشات لأدمن، هيتواصل مع العميل قريب.";
                    break;
                }
            default:
                toolResultText = "أداة غير معروفة.";
                break;
        }

        return toolResultText;
    }
}
