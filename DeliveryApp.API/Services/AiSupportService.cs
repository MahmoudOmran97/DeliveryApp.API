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
    Task<AiReplyResult> GetReplyAsync(SupportSession session, IReadOnlyList<SupportMessage> history, User customer);
}

// ─────────────────────────────────────────────────────────────────────────
// بيكلم OpenRouter (https://openrouter.ai) بصيغة متوافقة مع OpenAI Chat
// Completions API، باستخدام إعدادات AiSettings المتخزنة في الداتابيز (وبيتحكم
// فيها الأدمن من شاشة "إعدادات الـ AI"). الموديل بيتحدد كـ slug بتاع OpenRouter
// زي "openai/gpt-4o-mini" أو "anthropic/claude-3.5-sonnet" أو "google/gemini-2.0-flash-001".
// بيديله أداتين (function tools):
//   1) create_complaint    → يسجل شكوى رسمية باسم العميل في جدول Complaints
//   2) escalate_to_admin   → يحول الشات لأدمن حقيقي ويبعت إشعار للأدمن
// بشات مفتوح مع العميل ده (Notification.ActionUrl = "supportchat/{sessionId}").
// ─────────────────────────────────────────────────────────────────────────
public class AiSupportService : IAiSupportService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly INotificationDispatcher _dispatcher;
    private readonly IHubService _hub;

    // OpenRouter: نفس شكل OpenAI /v1/chat/completions
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";

    public AiSupportService(ApplicationDbContext context, IHttpClientFactory httpFactory,
        INotificationDispatcher dispatcher, IHubService hub)
    {
        _context = context;
        _httpFactory = httpFactory;
        _dispatcher = dispatcher;
        _hub = hub;
    }

    // صيغة OpenAI function-calling (اللي OpenRouter بيتوقعها في "tools")
    private static readonly object[] Tools = new object[]
    {
        new
        {
            type = "function",
            function = new
            {
                name = "create_complaint",
                description = "سجّل شكوى رسمية للعميل عن مشكلة حقيقية ذكرها (طلب متأخر، منتج فاسد أو ناقص، مندوب أساء التعامل، خصم غلط، إلخ). استخدمها لما تكون متأكد إن العميل بيشتكي فعلاً مش بس بيسأل سؤال عادي.",
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

    public async Task<AiReplyResult> GetReplyAsync(SupportSession session, IReadOnlyList<SupportMessage> history, User customer)
    {
        var settings = await _context.AiSettings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings == null || !settings.IsEnabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new AiReplyResult
            {
                ReplyText = "خدمة المساعد الذكي متوقفة دلوقتي، حاول تاني بعدين أو اطلب التحويل لأدمن."
            };
        }

        // في OpenAI/OpenRouter شكل الرسائل، الـ system بيبقى أول رسالة في الـ array نفسه
        var messages = new List<object>
        {
            new Dictionary<string, object> { ["role"] = "system", ["content"] = settings.SystemPrompt ?? "" }
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
        var result = new AiReplyResult();

        // أقصى حاجة دورتين: الأولى ممكن ترجع tool_calls، والتانية بترجع الرد النهائي
        // بعد ما ننفذ الأداة ونبعتلها نتيجتها كرسالة role=tool.
        for (int round = 0; round < 2; round++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = settings.Model,
                ["max_tokens"] = settings.MaxTokens,
                ["messages"] = messages,
                ["tools"] = Tools
            };

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");
            // OpenRouter بيستحسن الهيدرز دي (اختيارية) عشان يظهر التطبيق في لوحة تحكمهم
            request.Headers.Add("HTTP-Referer", "https://tawseela.app");
            request.Headers.Add("X-Title", "Tawseela Support Chat");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request);
            }
            catch (Exception)
            {
                result.ReplyText = "⚠️ في مشكلة في الاتصال بخدمة المساعد الذكي، حاول تاني كمان شوية.";
                return result;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                result.ReplyText = "⚠️ في مشكلة في المساعد الذكي دلوقتي، جرب تاني كمان شوية أو اطلب التحويل لأدمن.";
                return result;
            }

            using var doc = JsonDocument.Parse(responseBody);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
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
                result.ReplyText = textPart ?? "تمام 👍";
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

                string toolResultText;
                switch (name)
                {
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
}
